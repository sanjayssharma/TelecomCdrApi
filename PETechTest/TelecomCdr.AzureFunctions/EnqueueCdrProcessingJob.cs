using Hangfire;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace TelecomCdr.AzureFunctions
{
    public class EnqueueCdrProcessingJob
    {
        private readonly ILogger<EnqueueCdrProcessingJob> _logger;
        private readonly IBackgroundJobClient _backgroundJobClient;

        // Static constructor for one-time Hangfire client configuration.
        // This is a common pattern in Azure Functions to configure services that need a connection string.
        // Ensure HANGFIRE_SQL_CONNECTION_STRING is set in your Azure Function App's application settings.
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


        // Constructor injection for ILogger and IBackgroundJobClient.
        // For IBackgroundJobClient to be injected, you need to configure DI in your Function App's Program.cs.
        public EnqueueCdrProcessingJob(
            ILogger<EnqueueCdrProcessingJob> logger,
            IBackgroundJobClient backgroundJobClient) // Injected
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
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
            [BlobTrigger("cdr-uploads/{blobName}", Connection = "AZURE_STORAGE_CONNECTION_STRING")] byte[] blobContent, // Using byte[] to get content if needed, or Stream
            string blobName,
            FunctionContext context) // To access metadata
        {
            _logger.LogInformation("Azure Function Blob Trigger processed blob\n Name: {BlobName}", blobName);

            // Extract metadata - specifically the UploadCorrelationId
            // Metadata keys are case-insensitive in retrieval but often stored in a specific case.
            Guid uploadCorrelationId = Guid.Empty; // Default: UNKNOWN_CORRELATION_ID

            try
            {
                if (context.BindingContext.BindingData.TryGetValue("metadata", out var metadataObject) &&
                metadataObject is IReadOnlyDictionary<string, string> metadata)
                {
                    // Try to get it by common casing, then be more flexible
                    if (metadata.TryGetValue("UploadCorrelationId", out var id) ||
                        metadata.TryGetValue("uploadcorrelationid", out id) ||
                        metadata.TryGetValue("uploadCorrelationId", out id)) // common variations
                    {
                        Guid.TryParse(id, out uploadCorrelationId);
                        // Should we attempt to parse the ID. If it fails, log and throw?
                        //if (Guid.TryParse(id, out uploadCorrelationId))
                        //{
                        //    throw new InvalidOperationException($"Invalid UploadCorrelationId: {id} for the requested operation");
                        //}
                    }
                    else
                    {
                        _logger.LogWarning("Blob '{BlobName}' is missing 'UploadCorrelationId' in its metadata. Using default.", blobName);
                    }
                }
                else
                {
                    _logger.LogWarning("No metadata found for blob '{BlobName}'. Using default correlation ID.", blobName);
                }

                _logger.LogInformation("Enqueuing Hangfire job for blob: {BlobName}, Correlation ID: {CorrelationId}", blobName, uploadCorrelationId);

                // Enqueue the job. The job type Cdr.HangfireJobs.CdrFileProcessingJobs must be resolvable
                // by the Hangfire server. The method signature must match exactly.
                // The "cdr-uploads" container name is hardcoded here but could also come from config or metadata.
                string containerName = "cdr-uploads"; // Hardcoded for simplicity, should be made configurable.
                _backgroundJobClient.Enqueue<Hangfire.Jobs.CdrFileProcessingJobs>(
                    job => job.ProcessFileFromBlobAsync(blobName, containerName, uploadCorrelationId)
                );

                _logger.LogInformation("Successfully enqueued Hangfire job for blob: {BlobName}", blobName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enqueuing Hangfire job for blob: {BlobName}", blobName);
                // Depending on the error, you might want to implement a retry or dead-lettering mechanism for the function itself.
                throw; // Rethrow to mark function execution as failed.
            }
            await Task.CompletedTask; // If no async operations after enqueuing.
        }
    }
}
