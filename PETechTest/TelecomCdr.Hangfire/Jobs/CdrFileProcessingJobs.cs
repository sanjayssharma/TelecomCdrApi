using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;

namespace TelecomCdr.Hangfire.Jobs
{
    public class CdrFileProcessingJobs
    {
        private readonly IFileProcessingService _fileProcessingService;
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly ILogger<CdrFileProcessingJobs> _logger;

        // Dependencies are injected by Hangfire's job activator,
        // which should be configured to use your ASP.NET Core DI container.
        public CdrFileProcessingJobs(
            IFileProcessingService fileProcessingService,
            IJobStatusRepository jobStatusRepository,
            ILogger<CdrFileProcessingJobs> logger)
        {
            _fileProcessingService = fileProcessingService ?? throw new ArgumentNullException(nameof(fileProcessingService));
            _jobStatusRepository = jobStatusRepository ?? throw new ArgumentNullException(nameof(jobStatusRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Processes a file (or a chunk of a file) from blob storage.
        /// </summary>
        /// <param name="blobName">The name of the blob to process.</param>
        /// <param name="containerName">The container where the blob resides.</param>
        /// <param name="jobCorrelationId">
        /// The correlation ID for this specific processing job.
        /// If this is a chunk, this will be the chunk's unique CorrelationId.
        /// If this is a single file, this will be the original UploadCorrelationId.
        /// </param>
        public async Task ProcessFileFromBlobAsync(string containerName, string blobName, Guid jobCorrelationId)
        {
            _logger.LogInformation("Hangfire job started: Processing blob '{BlobName}' from container '{ContainerName}' with Correlation ID '{CorrelationId}'.",
                blobName, containerName, jobCorrelationId);

            if (string.IsNullOrWhiteSpace(blobName))
            {
                _logger.LogError("Blob name cannot be null or whitespace for Hangfire job.");
                // Consider how to handle this error - perhaps a specific exception type
                // or moving the job to a dead-letter queue if Hangfire supports it.
                throw new ArgumentNullException(nameof(blobName));
            }
            if (string.IsNullOrWhiteSpace(containerName))
            {
                _logger.LogError("Container name cannot be null or whitespace for Hangfire job processing blob {BlobName}.", blobName);
                throw new ArgumentNullException(nameof(containerName));
            }

            var currentJobStatus = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(jobCorrelationId);
            if (currentJobStatus == null)
            {
                _logger.LogError($"JobStatus not found for JobCorrelationId: {jobCorrelationId}. Cannot process blob {blobName}.");
                // This indicates a problem, as the JobStatus should have been created before enqueuing.
                // Depending on policy, could attempt to create one or simply fail.
                return;
            }

            try
            {
                // Update current job (single file or chunk) status to Processing
                currentJobStatus.Status = ProcessingStatus.Processing;
                currentJobStatus.LastUpdatedAtUtc = DateTime.UtcNow;
                currentJobStatus.Message = "Job picked up by Hangfire worker and is now processing.";

                await _jobStatusRepository.UpdateJobStatusAsync(currentJobStatus);

                _logger.LogInformation($"JobStatus for {jobCorrelationId} updated to Processing.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update job status to 'Processing' for Correlation ID '{CorrelationId}'. Processing will continue but status might be stale.", jobCorrelationId);
            }

            FileProcessingResult? processingResult = default;

            try
            {
                // The IFileProcessingService handles downloading from blob, parsing, and storing.
                processingResult = await _fileProcessingService.ProcessAndStoreCdrFileFromBlobAsync(containerName, blobName, jobCorrelationId);

                var successMessage = $"File processed. Successful records: {processingResult.ProcessedRecordsCount}, Failed records: {processingResult.FailedRecordsCount}.";
                if (processingResult.ErrorMessages.Any())
                {
                    successMessage += $" Some errors: {string.Join("; ", processingResult.ErrorMessages.Take(3))}"; // Show first few errors
                }

                currentJobStatus.ProcessedRecordsCount = processingResult.ProcessedRecordsCount;
                currentJobStatus.FailedRecordsCount = processingResult.FailedRecordsCount;

                var finalStatus = ProcessingStatus.Failed; // Default to Failed
                if (processingResult.ProcessedRecordsCount > 0 && processingResult.FailedRecordsCount == 0)
                {
                    finalStatus = ProcessingStatus.Succeeded;
                }
                else if (processingResult.ProcessedRecordsCount > 0 && processingResult.FailedRecordsCount > 0)
                {
                    finalStatus = ProcessingStatus.PartiallySucceeded;
                }

                // If ProcessedRecordsCount is 0 and FailedRecordsCount > 0, it remains Failed.
                // If both are 0 but there were general file errors (e.g. file not found), it also remains Failed.

                await _jobStatusRepository.UpdateJobStatusAsync(
                    jobCorrelationId,
                    finalStatus,
                    successMessage.Length > 2000 ? successMessage.Substring(0, 2000) : successMessage, // Truncate message
                    processingResult.ProcessedRecordsCount,
                    processingResult.FailedRecordsCount);

                _logger.LogInformation("Hangfire job processing outcome for Correlation ID '{CorrelationId}': {Status} - {Message}",
                    jobCorrelationId, finalStatus, successMessage);

                // If there were critical errors that should fail the Hangfire job itself (for retries)
                if (finalStatus == ProcessingStatus.Failed && processingResult.ProcessedRecordsCount == 0 && processingResult.FailedRecordsCount == -1 && processingResult.ErrorMessages.Any(m => m.Contains("Blob") && m.Contains("not found")))
                {
                    // This indicates a file level error, not just row errors.
                    throw new InvalidOperationException($"Critical file processing error for {blobName}: {string.Join("; ", processingResult.ErrorMessages)}");
                }

                _logger.LogInformation("Hangfire job completed: Successfully processed blob '{BlobName}' with Correlation ID '{CorrelationId}'.",
                    blobName, jobCorrelationId);
            }
            catch (Exception ex) // Catch exceptions from ProcessAndStoreCdrFileFromBlobAsync or status updates
            {
                _logger.LogError(ex, "Hangfire job CRITICAL FAILURE: Error processing blob '{BlobName}' with Correlation ID '{CorrelationId}'.",
                    blobName, jobCorrelationId);
                try
                {
                    await _jobStatusRepository.UpdateJobStatusAsync(
                        jobCorrelationId,
                        ProcessingStatus.Failed,
                        $"Critical processing error: {ex.Message.Substring(0, Math.Min(ex.Message.Length, 1900))}", // Truncate
                        processingResult?.ProcessedRecordsCount ?? 0, // No successful records confirmed
                        processingResult?.FailedRecordsCount ?? 0 // Unknown failed records if this is a general crash
                        );
                }
                catch (Exception statusEx)
                {
                    _logger.LogError(statusEx, "Additionally, failed to update job status to 'Failed' after CRITICAL error for Correlation ID '{CorrelationId}'.", jobCorrelationId);
                }
                throw; // Rethrow original exception for Hangfire to handle (e.g., retry)
            }
            finally
            {
                // Reload the currentJobStatus to get the latest updates made by UpdateJobStatusProcessingResultAsync
                currentJobStatus = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(jobCorrelationId);

                // If this was a chunk, update the master job status
                if (currentJobStatus != null && currentJobStatus.Type == JobType.Chunk && currentJobStatus.ParentCorrelationId != Guid.Empty)
                {
                    _logger.LogInformation($"Processing completed for chunk {jobCorrelationId} (Blob: {blobName}). Updating master job {currentJobStatus.ParentCorrelationId}.");
                    try
                    {
                        await _jobStatusRepository.IncrementProcessedChunkCountAsync(
                            currentJobStatus.ParentCorrelationId.Value,
                            currentJobStatus.Status == ProcessingStatus.Succeeded, // chunkSucceeded
                            currentJobStatus.ProcessedRecordsCount ?? 0,          // chunkProcessedRecords
                            currentJobStatus.FailedRecordsCount ?? 0              // chunkFailedRecords
                        );
                        _logger.LogInformation($"Master job {currentJobStatus.ParentCorrelationId} updated based on chunk {jobCorrelationId} result.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to update master job status for ParentCorrelationId {currentJobStatus.ParentCorrelationId} after processing chunk {jobCorrelationId}: {ex.Message}");
                    }
                }
                else if (currentJobStatus != null && currentJobStatus.Type == JobType.SingleFile)
                {
                    _logger.LogInformation($"Processing completed for single file {jobCorrelationId} (Blob: {blobName}). No master job to update.");
                }
            }
        }
    }
}
