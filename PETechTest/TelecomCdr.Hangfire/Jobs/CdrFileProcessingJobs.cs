using Microsoft.Extensions.Logging;
using TelecomCdr.Core.Interfaces;
using TelecomCdr.Core.Models.DomainModel;

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
                // It needs to be implemented in Cdr.Infrastructure.
                await _fileProcessingService.ProcessAndStoreCdrFileFromBlobAsync(blobName, containerName, uploadCorrelationId);

                _logger.LogInformation("Hangfire job completed: Successfully processed blob '{BlobName}' with Correlation ID '{CorrelationId}'.",
                    blobName, uploadCorrelationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hangfire job failed: Error processing blob '{BlobName}' with Correlation ID '{CorrelationId}'.",
                    blobName, uploadCorrelationId);

                try
                {
                    // *** Usage Point 5: Updating status to Failed ***
                    await _jobStatusRepository.UpdateStatusAsync(uploadCorrelationId, ProcessingStatus.Failed, $"Processing failed: {ex.Message}");
                }
                catch (Exception statusEx)
                {
                    _logger.LogError(statusEx, "Additionally, failed to update job status to 'Failed' for Correlation ID '{CorrelationId}'.", uploadCorrelationId);
                }
                // Rethrow the exception so Hangfire can mark the job as failed and retry according to its policy.
                throw;
            }
        }
    }
}
