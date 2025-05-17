using Hangfire;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.Domain.Helpers;

namespace TelecomCdr.AzureFunctions.Functions
{
    public class EnqueueCdrProcessingJob
    {
        private readonly IBlobStorageService _blobStorageService;
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly IBlobProcessingOrchestrator _blobProcessingOrchestrator;
        private readonly ILogger<EnqueueCdrProcessingJob> _logger;

        private readonly string _defaultContainerName;
        private const string UploadCorrelationIdKey = "UploadCorrelationId";

        public EnqueueCdrProcessingJob(
            IBlobStorageService blobStorageService,
            IJobStatusRepository jobStatusRepository,
            IConfiguration configuration,
            IBlobProcessingOrchestrator blobProcessingOrchestrator,
            ILogger<EnqueueCdrProcessingJob> logger)
        {
            _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
            _jobStatusRepository = jobStatusRepository ?? throw new ArgumentNullException(nameof(jobStatusRepository));
            _blobProcessingOrchestrator = blobProcessingOrchestrator ?? throw new ArgumentNullException(nameof(blobProcessingOrchestrator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _defaultContainerName = configuration.GetValue<string>("AzureBlobStorage:ContainerName") ?? "cdr-uploads";
        }

        // Static constructor for one-time Hangfire client configuration.
        // This is a common pattern in Azure Functions to configure services that need a connection string.
        // HANGFIRE_SQL_CONNECTION_STRING is set in our Azure Function App's application settings.
        static EnqueueCdrProcessingJob()
        {
            var hangfireConnectionString = Environment.GetEnvironmentVariable("HANGFIRE_SQL_CONNECTION_STRING");
            if (string.IsNullOrEmpty(hangfireConnectionString))
            {
                // Log or handle critical error: Hangfire cannot be configured.
                // This will likely cause the function to fail at runtime if IBackgroundJobClient is used.
                Console.Error.WriteLine("FATAL: HANGFIRE_SQL_CONNECTION_STRING environment variable is not set. Hangfire client cannot be configured.");
            }
            else
            {
                // Ensure Hangfire storage is configured only once
                try
                {
                    GlobalConfiguration.Configuration.UseSqlServerStorage(hangfireConnectionString);
                    Console.WriteLine("Hangfire SQL Server storage configured.");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("JobStorage.Current"))
                {
                    // This exception might occur if UseSqlServerStorage is called multiple times.
                    // In a static constructor context, this should ideally run only once per process lifetime.
                    // Log it for awareness but might not be fatal if already configured.
                    Console.WriteLine("Hangfire storage seems to be already configured.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FATAL: Error configuring Hangfire storage: {ex.Message}");
                    // Rethrow or handle appropriately depending on whether the function can proceed
                    throw;
                }
            }
        }

        /// <summary>
        /// Azure Function triggered by a new blob in the 'cdr-uploads' container.
        /// It enqueues a Hangfire job to process the CDR file.
        /// </summary>
        /// <param name="blobStream">The stream of the uploaded blob. Not directly used here, but part of the trigger binding.</param>
        /// <param name="blobName">The name of the blob that triggered the function.</param>
        /// <param name="blobMetadata">Metadata associated with the blob.</param>
        [Function(nameof(EnqueueCdrProcessingJob))]
        public async Task Run(
            [BlobTrigger("cdr-uploads/{blobName}", Connection = "AZURE_STORAGE_CONNECTION_STRING")] Stream myBlob, // Stream might not be directly usable for size if large, properties are better
            string blobName, // Name of the blob
            Uri blobUri, // Full URI of the blob
            IDictionary<string, string> metadata // Blob metadata
            )
        {
            _logger.LogInformation($"C# Blob trigger function processed blob\n Name:{blobName} \n Uri: {blobUri}");

            // Extract container name from blobUri or configuration (simplified here)
            // For example, if blobUri is "https://account.blob.core.windows.net/cdr-uploads/filename.csv"
            // containerName would be "cdr-uploads".
            var containerName = blobUri?.Segments.Length > 1 ? blobUri.Segments[1].TrimEnd('/') : _defaultContainerName; // Basic extraction

            var (correlationIdExists, uploadCorrelationId) = GetCorrelationIdFromMetadata(metadata);
            if (!correlationIdExists)
            {
                _logger.LogError($"Invalid 'UploadCorrelationId' in blob metadata for {blobName}. Processing cannot continue.");
                return;
            }

            metadata.TryGetValue("OriginalFileName", out var originalFileName);
            metadata.TryGetValue("ParentCorrelationId", out var parentCorrelationIdValue);

            originalFileName = string.IsNullOrEmpty(originalFileName) ? blobName : originalFileName;
            Guid? parentCorrelationId = string.IsNullOrEmpty(parentCorrelationIdValue) ? null : Guid.Parse(parentCorrelationIdValue);

            try
            {
                var blobProperties = await _blobStorageService.GetBlobPropertiesAsync(containerName, blobName);
                long blobSize = blobProperties.Size;

                var triggerInfo = new BlobTriggerInfo
                {
                    BlobName = blobName,
                    ContainerName = containerName, // e.g., "cdr-uploads"
                    OriginalFileName = originalFileName,
                    BlobSize = blobSize,
                    Metadata = metadata.ToDictionary(),
                    UploadCorrelationId = uploadCorrelationId,
                    ParentCorrelationId = parentCorrelationId,
                };

                await _blobProcessingOrchestrator.OrchestrateBlobProcessingAsync(triggerInfo);
                _logger.LogInformation("Orchestration initiated for blob: {BlobName}", blobName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing blob {blobName} for CorrelationId {uploadCorrelationId}: {ex.Message}");
                // Attempt to update master job status to Failed
                try
                {
                    var jobStatusToFail = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(uploadCorrelationId);
                    if (jobStatusToFail != null)
                    {
                        await _jobStatusRepository.UpdateJobStatusAsync(jobStatusToFail
                            .WithStatus(ProcessingStatus.Failed)
                            .WithMessage($"Failed during initial processing or chunking: {ex.Message}"));
                    }
                }
                catch (Exception updateEx)
                {
                    _logger.LogError(updateEx, $"Failed to update job status to Failed for {uploadCorrelationId}: {updateEx.Message}");
                }
                // Depending on the error, we might want to rethrow or handle specifically
                // For an Azure Function, rethrowing might cause retries if configured.
            }
        }

        private (bool correlationIdExists, Guid correlationId) GetCorrelationIdFromMetadata(IDictionary<string, string> metadata)
        {
            var uploadCorrelationId = string.Empty;
            var correlationId = Guid.Empty;
            var correlationIdExists = metadata != null && metadata.TryGetValue(UploadCorrelationIdKey, out uploadCorrelationId);
            
            // Check if the correlation ID exists and is a valid GUID
            if (correlationIdExists && !Guid.TryParse(uploadCorrelationId, out correlationId))
            {
                _logger.LogError($"Invalid 'UploadCorrelationId' in blob metadata. Processing cannot continue.");
                return (false, correlationId);
            }

            return (correlationIdExists, correlationId);
        }
    }
}
