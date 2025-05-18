// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName={functionname}

using Azure.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.Domain.Helpers;

namespace TelecomCdr.AzureFunctions.Functions
{
    /// <summary>
    /// Azure Function triggered by Event Grid events when a new blob is created
    /// in the designated upload container. This function initiates the backend processing pipeline.
    /// </summary>
    public class BlobUploadProcessorFunction
    {
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly IBlobStorageService _blobStorageService;
        private readonly IBlobProcessingOrchestrator _blobProcessingOrchestrator;
        private readonly ILogger<BlobUploadProcessorFunction> _logger;

        public BlobUploadProcessorFunction(IJobStatusRepository jobStatusRepository,
            IBlobStorageService blobStorageService,
            IBlobProcessingOrchestrator blobProcessingOrchestrator,
            ILogger<BlobUploadProcessorFunction> logger)
        {
            _jobStatusRepository = jobStatusRepository ?? throw new ArgumentNullException(nameof(jobStatusRepository));
            _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
            _blobProcessingOrchestrator = blobProcessingOrchestrator ?? throw new ArgumentNullException(nameof(blobProcessingOrchestrator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Handles Event Grid events for blob creation.
        /// </summary>
        /// <param name="eventGridEvent">The event grid event.</param>
        /// <param name="context">The function context.</param>
        [Function(nameof(BlobUploadProcessorFunction))]
        public async Task Run([EventGridTrigger] CloudEvent eventGridEvent, FunctionContext functionContext)
        {
            _logger.LogInformation("BlobUploadProcessorFunction received EventGrid event. ID: {Id}, Type: {Type}, Subject: {Subject}",
                eventGridEvent.Id, eventGridEvent.Type, eventGridEvent.Subject);

            if (eventGridEvent.Data == null)
            {
                _logger.LogError("EventGridEvent data is null. Event ID: {Id}", eventGridEvent.Id);
                return;
            }

            // Event Grid events for Blob Storage have a specific schema for eventGridEvent.Data
            // We need to deserialize it to access properties like 'url', 'contentType', 'contentLength'.
            // The 'url' property usually contains the full path to the blob.
            // Example: "https://<storageaccountname>.blob.core.windows.net/<containername>/<blobname>"
            // The 'subject' property is usually "/blobServices/default/containers/<containername>/blobs/<blobname>"

            try
            {
                // Using JsonElement to parse the data. For strongly-typed, create a DTO matching StorageBlobCreatedEventData.
                var eventData = JsonDocument.Parse(eventGridEvent.Data.ToString()).RootElement;

                string blobUrl = eventData.TryGetProperty("url", out var url) ? url.GetString() : null;
                string contentType = eventData.TryGetProperty("contentType", out var ctype) ? ctype.GetString() : null;
                long contentLength = eventData.TryGetProperty("contentLength", out var cLength) && cLength.TryGetInt64(out var cl) ? cl : 0;

                if (string.IsNullOrWhiteSpace(blobUrl))
                {
                    _logger.LogError("Blob URL is missing in EventGridEvent data. Event ID: {Id}, Subject: {Subject}", eventGridEvent.Id, eventGridEvent.Subject);
                    return;
                }

                _logger.LogInformation("Processing blob created event for URL: {BlobUrl}, ContentType: {ContentType}, Length: {ContentLength}",
                    blobUrl, contentType, contentLength);

                // Extract ContainerName, BlobName, JobId, and OriginalFileName from the blob URL or subject
                // Assuming blob path is "containerName/jobId/originalFileName.csv"
                // And subject is "/blobServices/default/containers/containerName/blobs/jobId/originalFileName.csv"

                Uri uri = new Uri(blobUrl);
                string containerName = uri.Segments[1].TrimEnd('/'); // Segments[0] is "/", Segments[1] is "containerName/"

                // BlobName in the path includes the "jobId/originalFileName.csv" part
                string fullBlobNameInPath = string.Join("", uri.Segments.Skip(2)); // Skips "/" and "containerName/"

                if (string.IsNullOrWhiteSpace(fullBlobNameInPath) || !fullBlobNameInPath.Contains('/'))
                {
                    _logger.LogError("Could not parse JobId and OriginalFileName from blob path: {BlobPath}. Expected format 'JobId/OriginalFileName'. Event ID: {Id}", fullBlobNameInPath, eventGridEvent.Id);
                    return;
                }

                string jobId = Path.GetDirectoryName(fullBlobNameInPath);
                string originalFileName = Path.GetFileName(fullBlobNameInPath);

                if (string.IsNullOrWhiteSpace(jobId) || jobId.Contains('/') || string.IsNullOrWhiteSpace(originalFileName)) // Basic validation for jobId
                {
                    _logger.LogError("Invalid JobId or OriginalFileName parsed from blob path: {BlobPath}. JobId: '{ParsedJobId}', FileName: '{ParsedFileName}'. Event ID: {Id}",
                       fullBlobNameInPath, jobId, originalFileName, eventGridEvent.Id);
                    return;
                }

                var uploadCorrelationId = Guid.Empty;

                try
                {
                    var triggerInfo = await InitializeBlobMetadataAsync(containerName, fullBlobNameInPath, jobId, originalFileName);

                    await _blobProcessingOrchestrator.OrchestrateBlobProcessingAsync(triggerInfo);
                    _logger.LogInformation("Orchestration initiated for blob: {BlobName}", fullBlobNameInPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing blob {fullBlobNameInPath} for CorrelationId {jobId}: {ex.Message}");

                    // Attempt to update master job status to Failed
                    try
                    {
                        var jobStatusToFail = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(uploadCorrelationId);
                        if (jobStatusToFail != null)
                        {
                            await _jobStatusRepository.UpdateJobStatusAsync(jobStatusToFail
                                .WithStatus(ProcessingStatus.Failed)
                                .WithMessage($"Failed during initial processing or chunking for Job with CorrelationId {uploadCorrelationId}: {ex.Message}"));
                        }
                    }
                    catch (Exception updateEx)
                    {
                        _logger.LogError(updateEx, $"Failed to update job status to Failed for {jobId}, UploadCorrelationId {uploadCorrelationId}: {updateEx.Message}");
                    }
                    // Depending on the error, we might want to rethrow or handle specifically
                    // For an Azure Function, rethrowing might cause retries if configured.
                }

                _logger.LogInformation("Successfully dispatched ProcessBlobCommand for JobId: {JobId}, Blob: {BlobName}, UploadCorrelationId: {UploadCorrelationId}", jobId, fullBlobNameInPath, uploadCorrelationId);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Error deserializing EventGridEvent data. Event ID: {Id}, Data: {EventData}", eventGridEvent.Id, eventGridEvent.Data.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing EventGrid event for subject: {Subject}. Event ID: {Id}", eventGridEvent.Subject, eventGridEvent.Id);
                // Depending on the error, you might want to implement retry logic or dead-lettering
                // at the Event Grid subscription level or within the function.
                throw; // Rethrow to let Azure Functions runtime handle it (e.g., log to App Insights, retry if configured)
            }
        }

        private async Task<BlobTriggerInfo> InitializeBlobMetadataAsync(string containerName, string blobNameWithJobId, string jobId, string originalFileName)
        {
            _logger.LogInformation("Initializing blob metadata for blob: {BlobName} in container: {ContainerName}", blobNameWithJobId, containerName);

            var (blobSize, blobMetadata) = await _blobStorageService.GetBlobPropertiesAsync(containerName, blobNameWithJobId);
            var (IsClientGeneratedCorrelationId, uploadCorrelationId) = ParseCorrelationId(jobId);

            blobMetadata.Add("JobId", jobId);
            blobMetadata.Add("UploadCorrelationId", uploadCorrelationId.ToString());
            blobMetadata.Add("OriginalFileName", originalFileName);
            blobMetadata.Add("ParentCorrelationId", null);
            blobMetadata.Add("IsClientGeneratedCorrelationId", IsClientGeneratedCorrelationId.ToString());

            await _blobStorageService.SetMetadataAsync(containerName, blobNameWithJobId, blobMetadata);

            var triggerInfo = new BlobTriggerInfo
            {
                BlobName = blobNameWithJobId, // JobId/filename.csv
                ContainerName = containerName, // e.g., "direct-cdr-uploads"
                OriginalFileName = originalFileName,
                BlobSize = blobSize,
                Metadata = blobMetadata.ToDictionary(),
                UploadCorrelationId = uploadCorrelationId,
                ParentCorrelationId = null,
            };

            return triggerInfo;
        }

        private (bool isClientGeneratedCorrelationId, Guid uploadCorrelationId) ParseCorrelationId(string jobId)
        {
            var IsClientGeneratedCorrelationId = true;
            var uploadCorrelationId = Guid.Empty;

            if (!Guid.TryParse(jobId, out uploadCorrelationId))
            {
                IsClientGeneratedCorrelationId = false;
                uploadCorrelationId = Guid.NewGuid();
            }

            return (IsClientGeneratedCorrelationId, uploadCorrelationId);
        }
    }
}
