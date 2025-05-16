using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.DurableFunctions.Dtos;

namespace TelecomCdr.DurableFunctions.Activities
{
    public class ProcessChunkActivityFunction
    {
        private readonly IFileProcessingService _csvFileProcessingService;
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly ILogger<ProcessChunkActivityFunction> _logger;

        public ProcessChunkActivityFunction(
            IFileProcessingService csvFileProcessingService,
            IJobStatusRepository jobStatusRepository,
            ILogger<ProcessChunkActivityFunction> logger)
        {
            _csvFileProcessingService = csvFileProcessingService;
            _jobStatusRepository = jobStatusRepository;
            _logger = logger;
        }

        [Function(nameof(ProcessChunkActivityFunction))]
        public async Task<FileProcessingResult> Run([ActivityTrigger] ChunkInfo chunkInfo)
        {
            _logger.LogInformation("Processing chunk: {ChunkBlobName} (ChunkCorrelationId: {ChunkCorrelationId}, Parent: {ParentCorrelationId})",
                chunkInfo.ChunkBlobName, chunkInfo.ChunkCorrelationId,
                (await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(chunkInfo.ChunkCorrelationId))?.ParentCorrelationId?.ToString() ?? "N/A");

            FileProcessingResult result = new FileProcessingResult();
            ProcessingStatus finalChunkStatus = ProcessingStatus.Failed;
            string statusMessage = "Chunk processing encountered an error.";

            try
            {
                // Update this chunk's status to Processing
                await _jobStatusRepository.UpdateJobStatusAsync(chunkInfo.ChunkCorrelationId, ProcessingStatus.Processing, "Chunk processing started.", null);

                // Process the actual chunk data
                result = await _csvFileProcessingService.ProcessAndStoreCdrFileFromBlobAsync(
                    chunkInfo.ChunkBlobName,
                    chunkInfo.ChunkContainerName,
                    chunkInfo.ChunkCorrelationId // Pass chunk's own ID for its records
                );

                statusMessage = $"Chunk processed. Successful: {result.ProcessedRecordsCount}, Failed: {result.FailedRecordsCount}.";
                if (result.ErrorMessages.Any()) { statusMessage += $" Errors: {string.Join("; ", result.ErrorMessages.Take(2))}"; }

                if (result.ProcessedRecordsCount > 0 && result.FailedRecordsCount == 0 && result.FailedRecordsCount != -1)
                {
                    finalChunkStatus = ProcessingStatus.Succeeded;
                }
                else if (result.ProcessedRecordsCount > 0 && result.FailedRecordsCount > 0)
                {
                    finalChunkStatus = ProcessingStatus.PartiallySucceeded;
                }
                // else it remains Failed (default or if FailedRecords == -1)

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing chunk {ChunkBlobName} (ChunkCorrelationId: {ChunkCorrelationId})",
                    chunkInfo.ChunkBlobName, chunkInfo.ChunkCorrelationId);
                result.ErrorMessages.Add(ex.Message);
                result.FailedRecordsCount = (result.FailedRecordsCount == -1 || result.FailedRecordsCount == 0) ? 1 : result.FailedRecordsCount + 1; // Ensure at least one failure is noted
                finalChunkStatus = ProcessingStatus.Failed;
                statusMessage = $"Error processing chunk: {ex.Message}";
            }
            finally
            {
                // Update this chunk's final status
                await _jobStatusRepository.UpdateJobStatusAsync(
                    chunkInfo.ChunkCorrelationId,
                    finalChunkStatus,
                    statusMessage.Length > 2000 ? statusMessage.Substring(0, 2000) : statusMessage,
                    result.ProcessedRecordsCount,
                    result.FailedRecordsCount == -1 ? null : (int?)result.FailedRecordsCount
                );

                // Notify master job about this chunk's completion
                var chunkJobStatus = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(chunkInfo.ChunkCorrelationId);
                if (chunkJobStatus?.ParentCorrelationId != null)
                {
                    await _jobStatusRepository.IncrementProcessedChunkCountAsync(
                        chunkJobStatus.ParentCorrelationId,
                        finalChunkStatus == ProcessingStatus.Succeeded
                    );
                }
            }
            return result; // Return result to orchestrator
        }
    }
}
