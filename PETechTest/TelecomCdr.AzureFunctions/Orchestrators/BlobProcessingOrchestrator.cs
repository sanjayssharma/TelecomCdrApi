using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.Domain.Helpers;

namespace TelecomCdr.AzureFunctions.Orchestrators
{
    public class BlobProcessingOrchestrator : IBlobProcessingOrchestrator
    {
        private readonly IBlobStorageService _blobStorageService;
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<BlobProcessingOrchestrator> _logger;
        private readonly long _chunkThresholdBytes;
        private readonly long _targetChunkSizeBytes;

        public BlobProcessingOrchestrator(
            IBlobStorageService blobStorageService,
            IJobStatusRepository jobStatusRepository,
            IBackgroundJobClient backgroundJobClient,
            IConfiguration configuration, // To get threshold values
            ILogger<BlobProcessingOrchestrator> logger)
        {
            _blobStorageService = blobStorageService;
            _jobStatusRepository = jobStatusRepository;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;

            var chunkThresholdMb = configuration.GetValue<long>("FileProcessing:ChunkThresholdMegaBytes", 500); // Default 500MB
            _chunkThresholdBytes = chunkThresholdMb * 1024 * 1024; // Mb to Bytes

            var targetChunkSizeMb = configuration.GetValue<long>("FileProcessing:TargetChunkSizeMegaBytes", 200); // Default 200MB
            _targetChunkSizeBytes = targetChunkSizeMb * 1024 * 1024; // Mb to Bytes
        }

        public async Task OrchestrateBlobProcessingAsync(BlobTriggerInfo triggerInfo, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Orchestrating processing for blob: {BlobName}, CorrelationId: {CorrelationId}, Size: {BlobSize} bytes",
                triggerInfo.BlobName, triggerInfo.UploadCorrelationId, triggerInfo.BlobSize);

            var uploadJob = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(triggerInfo.UploadCorrelationId);

            // Create a new job record if it doesn't exist, This could be useful for a Direct-to-Blob upload scenario
            if (uploadJob == null)
            {
                _logger.LogError("Master JobStatus not found for CorrelationId {CorrelationId}. This might indicate an issue with initial job creation.", triggerInfo.UploadCorrelationId);
                // Potentially create a master job status here if it's absolutely missing,
                // or decide this is a fatal error for this blob.
                // For now, we assume the API created the initial master/singlefile job status.
                // If not, the API call to /upload-large should ensure it.
                // Let's assume it exists for orchestration. If not, the function should probably log and exit.
                _logger.LogWarning("Initial Master JobStatus not found for CorrelationId {CorrelationId}. Creating a new one.", triggerInfo.UploadCorrelationId);

                uploadJob = new JobStatusBuilder()
                    .WithCorrelationId(triggerInfo.UploadCorrelationId)
                    .WithParentCorrelationId(triggerInfo.ParentCorrelationId)
                    .WithOriginalFileName(triggerInfo.OriginalFileName)
                    .WithBlobName(triggerInfo.BlobName)
                    .WithContainerName(triggerInfo.ContainerName)
                    .WithStatus(ProcessingStatus.Accepted) // Default initial status
                    .WithType(JobType.SingleFile) // Will be updated if chunking
                    .WithProcessedRecordsCount(0)
                    .WithFailedRecordsCount(0)
                    .Build();

                await _jobStatusRepository.CreateJobStatusAsync(uploadJob);
            }

            // Don't re-chunk an already identified chunk
            if (IsChunkingRequired(triggerInfo.BlobSize, uploadJob.Type)) 
            {
                _logger.LogInformation("Blob {BlobName} exceeds threshold ({ThresholdBytes} bytes). Initiating chunking.",
                    triggerInfo.BlobName, _chunkThresholdBytes);

                // 1. Update Master Job Status to Chunking
                 // Ensure type is Master for chunking
                await _jobStatusRepository.UpdateJobStatusAsync(uploadJob.WithStatus(ProcessingStatus.Chunking)
                    .WithMessage($"File blobSize {triggerInfo.BlobSize} bytes exceeds threshold. Splitting into chunks.")
                    .WithType(JobType.Master)
                    .WithLastUpdatedAtUtc(DateTime.UtcNow));

                // 2. Perform Blob Splitting (Complex Logic) this will upload the chunks to the blob storage
                // Blob upload will start the orchestration for each chunk
                var chunkInfos = await SplitUploadBlobIntoChunksAsync(
                                            triggerInfo.ContainerName,
                                            triggerInfo.BlobName,
                                            triggerInfo.UploadCorrelationId,
                                            cancellationToken);

                if (chunkInfos.Count == 0)
                {
                    _logger.LogError("Chunking process for {BlobName} resulted in no chunks. Marking master job as failed.", triggerInfo.BlobName);
                    await _jobStatusRepository.UpdateJobStatusAsync(triggerInfo.UploadCorrelationId, ProcessingStatus.Failed, "Chunking failed to produce any chunks.", null);
                    return;
                }

                // 3. Update Master Job Status to ChunksQueued
                await _jobStatusRepository.UpdateJobStatusAsync(uploadJob.WithStatus(ProcessingStatus.ChunksQueued)
                    .WithMessage($"{chunkInfos.Count} chunks created and queued for processing.")
                    .WithTotalChunks(chunkInfos.Count)
                    .WithProcessedChunks(0)
                    .WithSuccessfulChunks(0)
                    .WithFailedChunks(0)
                    .WithLastUpdatedAtUtc(DateTime.UtcNow));

                _logger.LogInformation("Successfully chunked {BlobName} into {TotalChunks} chunks and enqueued processing jobs.",
                    triggerInfo.BlobName, chunkInfos.Count);

                // Optionally delete the original large blob after successful chunking
                // await _blobStorageService.DeleteFileAsync(triggerInfo.ContainerName, triggerInfo.BlobName);
            }
            else // File is small enough or is already a chunk, process as a single unit
            {
                if (uploadJob.IsChunkJob())
                {
                    _logger.LogInformation("Blob {BlobName} is a chunk. Enqueuing for direct processing with CorrelationId {CorrelationId}.",
                       triggerInfo.BlobName, triggerInfo.UploadCorrelationId); // UploadCorrelationId is the chunk's ID here
                }
                else
                {
                    _logger.LogInformation("Blob {BlobName} does not require chunking. Enqueuing as single file job with CorrelationId {CorrelationId}.",
                        triggerInfo.BlobName, triggerInfo.UploadCorrelationId);
                    uploadJob.Type = JobType.SingleFile; // Ensure type is SingleFile if not chunked
                }

                await _jobStatusRepository.UpdateJobStatusAsync(uploadJob.WithStatus(ProcessingStatus.QueuedForProcessing)
                    .WithMessage($"{uploadJob.Type} queued for processing as a single unit.")
                    .WithLastUpdatedAtUtc(DateTime.UtcNow));

                // Enqueue a hangfire processing job for the blob
                _backgroundJobClient.Enqueue<Hangfire.Jobs.CdrFileProcessingJobs>(
                    job => job.ProcessFileFromBlobAsync(triggerInfo.ContainerName, triggerInfo.BlobName, triggerInfo.UploadCorrelationId)
                );

                _logger.LogInformation("Enqueued single processing job for blob: {BlobName}, CorrelationId: {CorrelationId}",
                    triggerInfo.BlobName, triggerInfo.UploadCorrelationId);
            }
        }

        // Placeholder for the complex chunking logic
        private async Task<List<ChunkInfo>> SplitUploadBlobIntoChunksAsync(
            string containerName, string blobName, Guid parentCorrelationId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting to split blob {OriginalBlobName} in container {ContainerName}.", blobName, containerName);
            var chunkInfos = new List<ChunkInfo>();
            int chunkNumber = 1;

            try
            {
                using var blobStream = await _blobStorageService.DownloadFileAsync(containerName, blobName, cancellationToken);
                if (blobStream == null || blobStream == Stream.Null)
                {
                    _logger.LogError("Original blob {OriginalBlobName} could not be downloaded for chunking.", blobName);
                    return chunkInfos; // Return empty list
                }

                using var streamReader = new StreamReader(blobStream, Encoding.UTF8, true);
                string? headerLine = null;
                if (streamReader.Peek() >= 0) // Check if stream is not empty
                {
                    headerLine = await streamReader.ReadLineAsync(cancellationToken);
                }

                if (string.IsNullOrEmpty(headerLine))
                {
                    _logger.LogWarning("Blob {OriginalBlobName} is empty or header could not be read. No chunks created.", blobName);
                    return chunkInfos;
                }

                while (streamReader.Peek() >= 0) // While there's more data
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentChunkLines = new List<string> { headerLine }; // Each chunk gets the header
                    long currentChunkSize = Encoding.UTF8.GetByteCount(headerLine + Environment.NewLine);

                    string? line;
                    // Read lines until we reach the target chunk size or end of stream
                    while ((line = await streamReader.ReadLineAsync(cancellationToken)) != null)
                    {
                        currentChunkLines.Add(line);
                        currentChunkSize += Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                        if (currentChunkSize >= _targetChunkSizeBytes)
                        {
                            break; // Current chunk is large enough
                        }
                    }

                    // If we are here, we have a chunk data ready to be uploaded
                    if (currentChunkLines.Count > 1) // More than just the header
                    {
                        string chunkContent = string.Join(Environment.NewLine, currentChunkLines);
                        using var chunkStream = new MemoryStream(Encoding.UTF8.GetBytes(chunkContent));

                        var chunkBlobName = $"{Path.GetFileNameWithoutExtension(blobName)}_chunk{chunkNumber}{Path.GetExtension(blobName)}";
                        var chunkCorrelationId = Guid.NewGuid(); // Unique ID for this chunk's job status

                        // 1. Create a new job status for this chunk
                        await CreateNewChunkJobEntryAsync(chunkCorrelationId, parentCorrelationId, chunkBlobName,
                            containerName, ProcessingStatus.Accepted, $"Chunk {chunkNumber} of {blobName} created.");

                        // 2. Add chunk info to the list, would be useful in case of failures when uploading blob
                        chunkInfos.Add(new ChunkInfo
                        {
                            ChunkBlobName = chunkBlobName,
                            ChunkContainerName = containerName, // Or chunks container
                            ChunkCorrelationId = chunkCorrelationId,
                            ChunkNumber = chunkNumber
                        });

                        // 3. Upload the chunk
                        await _blobStorageService.UploadFileAsync(
                            containerName, // Or a dedicated chunks container
                            chunkBlobName,
                            chunkStream,
                            metadata: new Dictionary<string, string> {
                                { "ParentCorrelationId", parentCorrelationId.ToString() },
                                { "UploadCorrelationId", chunkCorrelationId.ToString() },
                                { "ChunkNumber", chunkNumber.ToString() },
                                { "IsChunk", "true" }
                            });
                        
                        _logger.LogInformation("Created and uploaded chunk {ChunkNumber}: {ChunkBlobName} for master {MasterCorrelationId} with correlationId {ChunkCorrelationId}", chunkNumber, chunkBlobName, parentCorrelationId, chunkCorrelationId);
                        chunkNumber++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await RollbackChunksAsync(parentCorrelationId, chunkInfos);

                _logger.LogWarning("Blob splitting was cancelled for {OriginalBlobName}.", blobName);
                throw;
            }
            catch (Exception ex)
            {
                await RollbackChunksAsync(parentCorrelationId, chunkInfos);

                _logger.LogError(ex, "Error during blob splitting for {OriginalBlobName}.", blobName);
                // Depending on policy, might clear chunkInfos or rethrow to fail the master job
                chunkInfos.Clear(); // Don't process partial chunks if splitting fails catastrophically
                throw; // Rethrow to let the orchestrator handle this failure
            }
            return chunkInfos;
        }

        private async Task RollbackChunksAsync(Guid parentCorrelationId, IEnumerable<ChunkInfo> chunkInfos)
        {
            _logger.LogWarning("Rollback initiated for chunks related to ParentCorrelationId {ParentCorrelationId}.", parentCorrelationId);

            // Add rollback logic,
            // Complete cleanup e.g., delete any created chunks Database and BlobStorage
            // Or mark them as failed in the database and delete from blob storage
        }

        private bool IsChunkingRequired(long blobSize, JobType jobType)
        {
            return blobSize > _chunkThresholdBytes && jobType != JobType.Chunk;
        }

        private async Task CreateNewChunkJobEntryAsync(Guid chunkCorrelationId, Guid masterCorrelationId, string chunkBlobName, string containerName, ProcessingStatus status, string message)
        {
            _logger.LogInformation("Creating new chunk job entry for {ChunkBlobName} with CorrelationId {ChunkCorrelationId}.", chunkBlobName, chunkCorrelationId);
            var chunkJobStatus = new JobStatusBuilder()
                .WithCorrelationId(chunkCorrelationId)
                .WithParentCorrelationId(masterCorrelationId)
                .WithType(JobType.Chunk)
                .WithStatus(status)
                .WithMessage(message)
                .WithOriginalFileName(chunkBlobName)
                .WithBlobName(chunkBlobName)
                .WithContainerName(containerName)
                .WithProcessedRecordsCount(0)
                .WithFailedRecordsCount(0)
                .Build();

            await _jobStatusRepository.CreateJobStatusAsync(chunkJobStatus);
        }
    }

    public class ChunkInfo // Helper class for SplitBlobIntoChunksAsync
    {
        public string ChunkBlobName { get; set; } = string.Empty;
        public string ChunkContainerName { get; set; } = string.Empty;
        public Guid ChunkCorrelationId { get; set; } = Guid.Empty; // Unique ID for this chunk's JobStatus
        public int ChunkNumber { get; set; }
    }
}
