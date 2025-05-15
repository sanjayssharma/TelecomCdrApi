using Hangfire;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Runtime.ConstrainedExecution;
using System.Text;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Domain;

namespace TelecomCdr.AzureFunctions
{
    public class EnqueueCdrProcessingJob
    {
        private const long CHUNK_THRESHOLD_BYTES = 500 * 1024 * 1024; // 500MB
        private const long TARGET_CHUNK_SIZE_BYTES = 100 * 1024 * 1024; // 100MB (adjust as needed)

        private readonly IBlobStorageService _blobStorageService;
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly IBackgroundJobClient _backgroundJobClient;

        public EnqueueCdrProcessingJob(
            IBlobStorageService blobStorageService,
            IJobStatusRepository jobStatusRepository,
            IBackgroundJobClient backgroundJobClient)
        {
            _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
            _jobStatusRepository = jobStatusRepository ?? throw new ArgumentNullException(nameof(jobStatusRepository));
            _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
        }

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
            IDictionary<string, string> metadata, // Blob metadata
            ILogger log)
        {
            log.LogInformation($"C# Blob trigger function processed blob\n Name:{blobName} \n Uri: {blobUri}");

            // Extract container name from blobUri or configuration (simplified here)
            // For example, if blobUri is "https://account.blob.core.windows.net/cdr-uploads/filename.csv"
            // containerName would be "cdr-uploads".
            var containerName = blobUri?.Segments.Length > 1 ? blobUri.Segments[1].TrimEnd('/') : "cdr-uploads"; // Basic extraction

            if (metadata == null || !metadata.TryGetValue("UploadCorrelationId", out var uploadCorrelationId) || string.IsNullOrEmpty(uploadCorrelationId))
            {
                log.LogError($"Missing 'UploadCorrelationId' in blob metadata for {blobName}. Processing cannot continue.");
                // Optionally, move to an error container or log to a dead-letter queue
                return;
            }

            if (!Guid.TryParse(uploadCorrelationId, out var correlationId))
            {
                log.LogError($"Invalid 'UploadCorrelationId' in blob metadata for {blobName}. Processing cannot continue.");
                return;
            }

            metadata.TryGetValue("OriginalFileName", out var originalFileName);
            originalFileName = string.IsNullOrEmpty(originalFileName) ? blobName : originalFileName;

            try
            {
                var blobProperties = await _blobStorageService.GetBlobPropertiesAsync(containerName, blobName);
                long blobSize = blobProperties.Size;

                JobStatus masterJobStatus = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(correlationId);
                if (masterJobStatus == null)
                {
                    // This case should ideally be handled: maybe the API failed to create the initial record.
                    // For now, we'll create a basic one, but a robust system would have specific error handling.
                    log.LogWarning($"Initial JobStatus not found for CorrelationId {correlationId}. Creating a new one.");
                    masterJobStatus = new JobStatus
                    {
                        CorrelationId = correlationId,
                        OriginalFileName = originalFileName,
                        BlobName = blobName,
                        ContainerName = containerName,
                        Status = ProcessingStatus.Accepted, // Default initial status
                        Type = JobType.SingleFile // Will be updated if chunking
                    };
                    await _jobStatusRepository.CreateJobStatusAsync(masterJobStatus);
                }
                else // Ensure essential details are set if they were missed
                {
                    masterJobStatus.OriginalFileName = masterJobStatus.OriginalFileName ?? originalFileName;
                    masterJobStatus.BlobName = masterJobStatus.BlobName ?? blobName;
                    masterJobStatus.ContainerName = masterJobStatus.ContainerName ?? containerName;
                }


                if (blobSize <= CHUNK_THRESHOLD_BYTES)
                {
                    log.LogInformation($"Blob {blobName} (Size: {blobSize} bytes) is below threshold. Processing as a single file.");
                    // Update status if it was just 'Accepted'
                    if (masterJobStatus.Status == ProcessingStatus.Accepted)
                    {
                        masterJobStatus.Status = ProcessingStatus.QueuedForProcessing;
                        masterJobStatus.Type = JobType.SingleFile; // Explicitly set as SingleFile
                        await _jobStatusRepository.UpdateJobStatusAsync(masterJobStatus);
                    }

                    _backgroundJobClient.Enqueue<Hangfire.Jobs.CdrFileProcessingJobs>(
                        job => job.ProcessFileFromBlobAsync(blobName, containerName, correlationId)
                    );
                    log.LogInformation($"Enqueued single Hangfire job for {blobName}, CorrelationId: {correlationId}.");
                }
                else
                {
                    log.LogInformation($"Blob {blobName} (Size: {blobSize} bytes) exceeds threshold. Starting chunking process.");

                    // Update Master Job Status for Chunking
                    masterJobStatus.Type = JobType.Master;
                    masterJobStatus.Status = ProcessingStatus.Chunking;
                    masterJobStatus.LastUpdatedAtUtc = DateTime.UtcNow;
                    await _jobStatusRepository.UpdateJobStatusAsync(masterJobStatus);

                    int totalChunksCreated = 0;
                    string headerLine = null;

                    using (var sourceStream = await _blobStorageService.DownloadFileAsync(containerName, blobName))
                    using (var reader = new StreamReader(sourceStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        // Strategy: Read header once, prepend to each chunk.
                        // Assumes CSV files where the first line is a header.
                        // If not all files have headers, or if processor handles missing headers, adjust this.
                        if (!reader.EndOfStream)
                        {
                            headerLine = await reader.ReadLineAsync();
                        }

                        if (string.IsNullOrEmpty(headerLine))
                        {
                            log.LogWarning($"Blob {blobName} is empty or has no header line. Chunking might produce empty chunks or fail.");
                            // Potentially mark master job as failed if header is mandatory
                        }

                        List<string> currentChunkLines = new List<string>();
                        long currentChunkSize = 0;
                        if (headerLine != null)
                        {
                            currentChunkSize += Encoding.UTF8.GetByteCount(headerLine + Environment.NewLine);
                        }


                        while (!reader.EndOfStream)
                        {
                            string line = await reader.ReadLineAsync();
                            if (line == null) break; // End of stream

                            currentChunkLines.Add(line);
                            currentChunkSize += Encoding.UTF8.GetByteCount(line + Environment.NewLine);

                            if (currentChunkSize >= TARGET_CHUNK_SIZE_BYTES || reader.EndOfStream)
                            {
                                totalChunksCreated++;
                                string chunkBlobName = $"{Path.GetFileNameWithoutExtension(originalFileName)}_chunk_{totalChunksCreated}{Path.GetExtension(originalFileName)}";
                                var chunkCorrelationId = Guid.NewGuid();

                                log.LogInformation($"Creating chunk {totalChunksCreated}: {chunkBlobName}, Size: {currentChunkSize} bytes");

                                // Construct chunk content (Header + Lines)
                                StringBuilder chunkContentBuilder = new StringBuilder();
                                if (headerLine != null)
                                {
                                    chunkContentBuilder.AppendLine(headerLine);
                                }
                                foreach (var chunkLine in currentChunkLines)
                                {
                                    chunkContentBuilder.AppendLine(chunkLine);
                                }
                                byte[] chunkBytes = Encoding.UTF8.GetBytes(chunkContentBuilder.ToString());

                                using (var chunkStream = new MemoryStream(chunkBytes))
                                {
                                    var chunkMetadata = new Dictionary<string, string>
                                    {
                                        { "ParentCorrelationId", correlationId.ToString() },
                                        { "ChunkNumber", totalChunksCreated.ToString() },
                                        { "IsChunk", "true" },
                                        { "OriginalFileName", originalFileName } // Good to have for context
                                    };
                                    await _blobStorageService.UploadFileAsync(containerName, chunkBlobName, chunkStream, chunkMetadata);
                                }

                                // Create Chunk Job Status
                                var chunkJobStatus = new JobStatus
                                {
                                    CorrelationId = chunkCorrelationId,
                                    ParentCorrelationId = correlationId,
                                    Type = JobType.Chunk,
                                    Status = ProcessingStatus.QueuedForProcessing,
                                    BlobName = chunkBlobName,
                                    ContainerName = containerName,
                                    OriginalFileName = originalFileName, // Store original file name for context
                                    CreatedAtUtc = DateTime.UtcNow,
                                    LastUpdatedAtUtc = DateTime.UtcNow
                                };
                                await _jobStatusRepository.CreateJobStatusAsync(chunkJobStatus);

                                // Enqueue Hangfire Job for Chunk
                                _backgroundJobClient.Enqueue<Hangfire.Jobs.CdrFileProcessingJobs>(
                                    job => job.ProcessFileFromBlobAsync(chunkBlobName, containerName, chunkCorrelationId)
                                );
                                log.LogInformation($"Enqueued Hangfire job for chunk {chunkBlobName}, ChunkCorrelationId: {chunkCorrelationId}.");

                                // Reset for next chunk
                                currentChunkLines.Clear();
                                currentChunkSize = 0;
                                if (headerLine != null)
                                {
                                    currentChunkSize += Encoding.UTF8.GetByteCount(headerLine + Environment.NewLine);
                                }
                            }
                        }
                    }

                    // Update Master Job Status After Chunking
                    masterJobStatus.Status = ProcessingStatus.ChunksQueued;
                    masterJobStatus.TotalChunks = totalChunksCreated;
                    masterJobStatus.ProcessedChunks = 0;
                    masterJobStatus.SuccessfulChunks = 0;
                    masterJobStatus.FailedChunks = 0;
                    masterJobStatus.LastUpdatedAtUtc = DateTime.UtcNow;
                    await _jobStatusRepository.UpdateJobStatusAsync(masterJobStatus);

                    log.LogInformation($"Finished chunking {blobName}. Total chunks created: {totalChunksCreated}. Master Job CorrelationId: {uploadCorrelationId}");

                    // Optional: Delete original large blob after successful chunking
                    // Consider the implications: if chunk processing fails, original might be needed.
                    // await _blobStorageService.DeleteBlobAsync(containerName, blobName);
                    // log.LogInformation($"Deleted original large blob: {blobName}");
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Error processing blob {blobName} for CorrelationId {uploadCorrelationId}: {ex.Message}");
                // Attempt to update master job status to Failed
                try
                {
                    var jobStatusToFail = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(correlationId);
                    if (jobStatusToFail != null)
                    {
                        jobStatusToFail.Status = ProcessingStatus.Failed;
                        jobStatusToFail.Message = $"Failed during initial processing or chunking: {ex.Message}";
                        jobStatusToFail.LastUpdatedAtUtc = DateTime.UtcNow;
                        await _jobStatusRepository.UpdateJobStatusAsync(jobStatusToFail);
                    }
                }
                catch (Exception updateEx)
                {
                    log.LogError(updateEx, $"Failed to update job status to Failed for {uploadCorrelationId}: {updateEx.Message}");
                }
                // Depending on the error, you might want to rethrow or handle specifically
                // For an Azure Function, rethrowing might cause retries if configured.
            }
        }
    }
}
