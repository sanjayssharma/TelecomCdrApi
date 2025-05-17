using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text.Json;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;

namespace TelecomCdr.AzureFunctions.Functions
{
    public class UpdateJobStatusFromQueue
    {
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly ILogger<UpdateJobStatusFromQueue> _logger;

        public UpdateJobStatusFromQueue(
            IJobStatusRepository jobStatusRepository,
            ILogger<UpdateJobStatusFromQueue> logger)
        {
            _jobStatusRepository = jobStatusRepository ?? throw new ArgumentNullException(nameof(jobStatusRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // The queue name here must match the one configured in AzureStorageQueueService
        // and in local.settings.json / App Settings (e.g., "job-status-updates")
        // Connection is the name of the App Setting holding the Azure Storage connection string.
        [Function(nameof(UpdateJobStatusFromQueue))]
        public async Task Run(
            [QueueTrigger("%JobStatusQueueName%", Connection = "AZURE_QUEUE_STORAGE_CONNECTION_STRING")] string queueMessage)
        {
            _logger.LogInformation("Queue trigger function processed message (first 1000 chars): {QueueMessageSnippet}",
                queueMessage.Length > 1000 ? queueMessage.Substring(0, 1000) + "..." : queueMessage);

            JobStatusUpdateMessage? statusUpdate;
            try
            {
                statusUpdate = JsonConvert.DeserializeObject<JobStatusUpdateMessage>(queueMessage);
                if (statusUpdate == null || statusUpdate.CorrelationId != Guid.Empty)
                {
                    _logger.LogError("Failed to deserialize queue message or CorrelationId is missing. Message: {QueueMessage}", queueMessage);
                    // Probably move to a dead-letter queue if deserialization fails.
                    return;
                }
            }
            catch (Newtonsoft.Json.JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSON deserialization error for queue message: {QueueMessage}", queueMessage);
                // Probably move to a dead-letter queue.
                return;
            }

            _logger.LogInformation("Processing job status update for CorrelationId: {CorrelationId}, DeterminedStatus: {DeterminedStatus}",
                statusUpdate.CorrelationId, statusUpdate.DeterminedStatus);

            try
            {
                // Update the specific job/chunk status
                await _jobStatusRepository.UpdateJobStatusAsync(
                    statusUpdate.CorrelationId,
                    statusUpdate.DeterminedStatus,
                    statusUpdate.DeterminedMessage,
                    statusUpdate.ProcessingResult.SuccessfulRecordsCount,
                    statusUpdate.ProcessingResult.FailedRecordsCount == -1 ? null : statusUpdate.ProcessingResult.FailedRecordsCount
                );
                _logger.LogInformation("Successfully updated status for Job/Chunk {CorrelationId} to {Status}.",
                    statusUpdate.CorrelationId, statusUpdate.DeterminedStatus);

                // If this was a chunk, update the master job status
                if (statusUpdate.JobType == JobType.Chunk && statusUpdate.ParentCorrelationId.HasValue && statusUpdate.ParentCorrelationId != Guid.Empty)
                {
                    _logger.LogInformation("Processing chunk completion for ParentCorrelationId: {ParentCorrelationId}, ChunkCorrelationId: {ChunkCorrelationId}",
                        statusUpdate.ParentCorrelationId, statusUpdate.CorrelationId);

                    bool chunkSucceeded = statusUpdate.DeterminedStatus == ProcessingStatus.Succeeded;
                    await _jobStatusRepository.IncrementProcessedChunkCountAsync(statusUpdate.ParentCorrelationId, chunkSucceeded);
                    // IncrementProcessedChunkCountAsync should internally call UpdateMasterJobStatusBasedOnChunksAsync
                    // when all chunks are processed.
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update job status in database for CorrelationId: {CorrelationId}. Message: {QueueMessage}",
                    statusUpdate.CorrelationId, queueMessage);
                // This message will be retried by the Functions runtime based on host.json configuration.
                // If it fails consistently, it will eventually go to the poison queue.
                throw; // Rethrow to allow Functions runtime to handle retries/poison queue.
            }
        }
    }
}
