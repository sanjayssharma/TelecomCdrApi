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
        /// Hangfire job method to process a CDR file from Azure Blob Storage.
        /// </summary>
        /// <param name="blobName">The name of the blob in Azure Storage.</param>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="uploadCorrelationId">The correlation ID associated with the original file upload.</param>
        /// <remarks>
        /// This method will be called by Hangfire. Ensure IFileProcessingService is registered
        /// in the DI container used by the Hangfire server.
        /// </remarks>
        public async Task ProcessFileFromBlobAsync(string blobName, string containerName, Guid uploadCorrelationId)
        {
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

            _logger.LogInformation("Hangfire job started: Processing blob '{BlobName}' from container '{ContainerName}' with Correlation ID '{CorrelationId}'.",
                blobName, containerName, uploadCorrelationId);

            FileProcessingResult processingResult;

            try
            {
                // *** Usage Point 3: Updating status to Processing ***
                await _jobStatusRepository.UpdateStatusAsync(uploadCorrelationId, ProcessingStatus.Processing, "Job picked up by Hangfire worker and is now processing.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update job status to 'Processing' for Correlation ID '{CorrelationId}'. Processing will continue but status might be stale.", uploadCorrelationId);
            }

            try
            {
                // The IFileProcessingService handles downloading from blob, parsing, and storing.
                processingResult = await _fileProcessingService.ProcessAndStoreCdrFileFromBlobAsync(blobName, containerName, uploadCorrelationId);

                string successMessage = $"File processed. Successful records: {processingResult.SuccessfulRecords}, Failed records: {processingResult.FailedRecords}.";
                if (processingResult.ErrorMessages.Any())
                {
                    successMessage += $" Some errors: {string.Join("; ", processingResult.ErrorMessages.Take(3))}"; // Show first few errors
                }

                ProcessingStatus finalStatus = ProcessingStatus.Failed; // Default to Failed
                if (processingResult.SuccessfulRecords > 0 && processingResult.FailedRecords == 0)
                {
                    finalStatus = ProcessingStatus.Succeeded;
                }
                else if (processingResult.SuccessfulRecords > 0 && processingResult.FailedRecords > 0)
                {
                    finalStatus = ProcessingStatus.PartiallySucceeded;
                }

                // If SuccessfulRecords is 0 and FailedRecords > 0, it remains Failed.
                // If both are 0 but there were general file errors (e.g. file not found), it also remains Failed.

                await _jobStatusRepository.UpdateStatusAsync(
                    uploadCorrelationId,
                    finalStatus,
                    successMessage.Length > 2000 ? successMessage.Substring(0, 2000) : successMessage, // Truncate message
                    processingResult.SuccessfulRecords,
                    processingResult.FailedRecords);

                _logger.LogInformation("Hangfire job processing outcome for Correlation ID '{CorrelationId}': {Status} - {Message}",
                    uploadCorrelationId, finalStatus, successMessage);

                // If there were critical errors that should fail the Hangfire job itself (for retries)
                if (finalStatus == ProcessingStatus.Failed && processingResult.SuccessfulRecords == 0 && processingResult.FailedRecords == 0 && processingResult.ErrorMessages.Any(m => m.Contains("Blob") && m.Contains("not found")))
                {
                    // This indicates a file level error, not just row errors.
                    throw new InvalidOperationException($"Critical file processing error for {blobName}: {string.Join("; ", processingResult.ErrorMessages)}");
                }

                _logger.LogInformation("Hangfire job completed: Successfully processed blob '{BlobName}' with Correlation ID '{CorrelationId}'.",
                    blobName, uploadCorrelationId);
            }
            catch (Exception ex) // Catch exceptions from ProcessAndStoreCdrFileFromBlobAsync or status updates
            {
                _logger.LogError(ex, "Hangfire job CRITICAL FAILURE: Error processing blob '{BlobName}' with Correlation ID '{CorrelationId}'.",
                    blobName, uploadCorrelationId);
                try
                {
                    await _jobStatusRepository.UpdateStatusAsync(
                        uploadCorrelationId,
                        ProcessingStatus.Failed,
                        $"Critical processing error: {ex.Message.Substring(0, Math.Min(ex.Message.Length, 1900))}", // Truncate
                        0, // No successful records confirmed
                        null // Unknown failed records if this is a general crash
                        );
                }
                catch (Exception statusEx)
                {
                    _logger.LogError(statusEx, "Additionally, failed to update job status to 'Failed' after CRITICAL error for Correlation ID '{CorrelationId}'.", uploadCorrelationId);
                }
                throw; // Rethrow original exception for Hangfire to handle (e.g., retry)
            }
        }
    }
}
