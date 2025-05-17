using Hangfire;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.Domain.Helpers;

namespace TelecomCdr.Hangfire.Jobs
{
    public class CdrFileProcessingJobs
    {
        private readonly IFileProcessingService _fileProcessingService;
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly IQueueService _queueService;
        private readonly ILogger<CdrFileProcessingJobs> _logger;

        // Dependencies are injected by Hangfire's job activator,
        // which should be configured to use our ASP.NET Core DI container.
        public CdrFileProcessingJobs(
            IFileProcessingService fileProcessingService,
            IJobStatusRepository jobStatusRepository,
            IQueueService queueService,
            ILogger<CdrFileProcessingJobs> logger)
        {
            _fileProcessingService = fileProcessingService ?? throw new ArgumentNullException(nameof(fileProcessingService));
            _jobStatusRepository = jobStatusRepository ?? throw new ArgumentNullException(nameof(jobStatusRepository));
            _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
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
        /// Automatic retries are disabled. On completion or unhandled error,
        /// finally, a message is sent to a queue for final status update.
        /// </param>
        [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
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
                _logger.LogError("JobStatus not found for JobCorrelationId: {JobCorrelationId}. Cannot process blob {BlobName}.", jobCorrelationId, blobName);
                // This indicates a problem, as the JobStatus should have been created before enqueuing.
                // Depending on policy, could attempt to create one or simply fail.
                return;
            }

            try
            {
                // Update current job (single file or chunk) status to Processing
                await _jobStatusRepository.UpdateJobStatusAsync(currentJobStatus
                    .WithStatus(ProcessingStatus.Processing)
                    .WithMessage("Job picked up by Hangfire worker and is now processing."));

                _logger.LogInformation("JobStatus for {JobCorrelationId} updated to Processing.", jobCorrelationId);
            }
            catch (Exception statusUpdateEx)
            {
                _logger.LogError(statusUpdateEx, "Failed to update job status to 'Processing' for Correlation ID '{JobCorrelationId}'. Processing will continue but status might be stale.", jobCorrelationId);
            }

            FileProcessingResult? processingResult = new();
            var determinedStatus = ProcessingStatus.Failed; // Default outcome
            var determinedMessage = "Processing did not complete as expected or an error occurred before result determination.";

            try
            {
                // The IFileProcessingService handles downloading from blob, parsing, and storing.
                processingResult = await _fileProcessingService.ProcessAndStoreCdrFileFromBlobAsync(containerName, blobName, jobCorrelationId);

                determinedMessage = $"File processed. Successful records: {processingResult.SuccessfulRecordsCount}, Failed records: {processingResult.FailedRecordsCount}.";
                if (processingResult.HasErrors)
                {
                    determinedMessage += $" Some errors: {string.Join("; ", processingResult.ErrorMessages.Take(3))}"; // Show first few errors
                }

                determinedStatus = processingResult.DetermineStatus();

                _logger.LogInformation("File processing service completed for Correlation ID '{JobCorrelationId}'. Determined Outcome: {Status} - {Message}",
                    jobCorrelationId, determinedStatus, determinedMessage);

                // If a critical file-level error occurred (e.g., file not found by CsvFileProcessingService)
                if (processingResult.HasCriticalErrors)
                {
                    _logger.LogError("Critical file-level error for {BlobName}, CorrelationId {JobCorrelationId}. Details: {ErrorMessages}. This status will be sent to the queue.",
                        blobName, jobCorrelationId, string.Join("; ", processingResult.ErrorMessages));
                }
            }
            catch (Exception ex) // Catch exceptions from ProcessAndStoreCdrFileFromBlobAsync or status updates
            {
                _logger.LogError(ex, "Hangfire job CRITICAL FAILURE: Error processing blob '{BlobName}' with Correlation ID '{JobCorrelationId}'.",
                    blobName, jobCorrelationId);

                determinedStatus = ProcessingStatus.Failed; // Ensure status is Failed
                determinedMessage = $"Unhandled processing error: {ex.Message.Substring(0, Math.Min(ex.Message.Length, 1000))}"; // Truncate
                processingResult.ErrorMessages.Add(determinedMessage); // Add to processing result for queue message
                processingResult.FailedRecordsCount = currentJobStatus?.Type == JobType.Master ? (currentJobStatus.TotalChunks ?? 1) : 1; // Mark all as failed in this context
                processingResult.SuccessfulRecordsCount = 0;
                
                // throw; // Hangfire should not retry the job
            }
            finally // Ensure message is sent to queue regardless of processing outcome
            {
                try
                {
                    // Update the job status in the database with the final outcome
                    await _jobStatusRepository.UpdateJobStatusAsync(currentJobStatus
                    .WithMessage(determinedMessage)
                    .WithStatus(determinedStatus)
                    .WithProcessedRecordsCount(processingResult.SuccessfulRecordsCount)
                    .WithFailedRecordsCount(processingResult.FailedRecordsCount));

                     await _jobStatusRepository.IncrementProcessedChunkCountAsync(currentJobStatus.ParentCorrelationId, determinedStatus == ProcessingStatus.Succeeded);

                    //// Construct the message for the queue which will update the job status
                    //var statusUpdateMessage = new JobStatusUpdateMessage
                    //{
                    //    CorrelationId = jobCorrelationId, // This is the ID of the current job/chunk
                    //    ProcessingResult = processingResult,
                    //    ParentCorrelationId = currentJobStatus?.ParentCorrelationId, // Pass along if it's a chunk
                    //    JobType = currentJobStatus?.Type ?? JobType.SingleFile,     // Pass job type
                    //    DeterminedStatus = determinedStatus,
                    //    DeterminedMessage = determinedMessage.Length > 2000 ? determinedMessage.Substring(0, 2000) : determinedMessage
                    //};

                    //// *** SENDING THE MESSAGE TO THE QUEUE ***
                    //await _queueService.SendJobStatusUpdateAsync(statusUpdateMessage);
                    //_logger.LogInformation("Successfully sent job status update to queue for CorrelationId: {JobCorrelationId}. Status: {Status}",
                    //    jobCorrelationId, determinedStatus);
                }
                catch (Exception queueEx)
                {
                    _logger.LogCritical(queueEx, "CRITICAL FAILURE: Failed to send job status update to queue for JobCorrelationId: {CorrelationId}. Status update might be lost. Determined Status was {DeterminedStatus}.",
                        jobCorrelationId, determinedStatus);
                    // If sending to the queue fails, this is a critical problem.
                    // The Hangfire job will be marked as failed due to [AutomaticRetry(Attempts=0)]
                    // because this exception will propagate out of the 'finally' block.
                    throw; // Rethrow to ensure Hangfire marks this job instance as failed.
                }
            }
        }
    }
}
