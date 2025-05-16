using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;

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
                triggerInfo.BlobName, triggerInfo.OriginalUploadCorrelationId, triggerInfo.BlobSize);

            var masterJobStatus = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(triggerInfo.OriginalUploadCorrelationId);
            if (masterJobStatus == null)
            {
                _logger.LogError("Master JobStatus not found for CorrelationId {CorrelationId}. This might indicate an issue with initial job creation.", triggerInfo.OriginalUploadCorrelationId);
                // Potentially create a master job status here if it's absolutely missing,
                // or decide this is a fatal error for this blob.
                // For now, we assume the API created the initial master/singlefile job status.
                // If not, the API call to /upload-large should ensure it.
                // Let's assume it exists for orchestration. If not, the function should probably log and exit.
                _logger.LogWarning("Initial Master JobStatus not found for CorrelationId {CorrelationId}. Creating a new one.", triggerInfo.OriginalUploadCorrelationId);

                masterJobStatus = new JobStatus
                {
                    CorrelationId = triggerInfo.OriginalUploadCorrelationId,
                    OriginalFileName = triggerInfo.OriginalFileName,
                    BlobName = triggerInfo.BlobName,
                    ContainerName = triggerInfo.ContainerName,
                    Status = ProcessingStatus.Accepted, // Default initial status
                    Type = JobType.SingleFile // Will be updated if chunking
                };
                await _jobStatusRepository.CreateJobStatusAsync(masterJobStatus);
            }

            if (triggerInfo.BlobSize > _chunkThresholdBytes && masterJobStatus.Type != JobType.Chunk) // Don't re-chunk an already identified chunk
            {
                _logger.LogInformation("Blob {BlobName} exceeds threshold ({ThresholdBytes} bytes). Initiating chunking.",
                    triggerInfo.BlobName, _chunkThresholdBytes);

                // 1. Update Master Job Status to Chunking
                masterJobStatus.Status = ProcessingStatus.Chunking;
                masterJobStatus.Message = $"File size {triggerInfo.BlobSize} bytes exceeds threshold. Splitting into chunks.";
                masterJobStatus.Type = JobType.Master; // Ensure it's marked as Master
                await _jobStatusRepository.UpdateJobStatusAsync(masterJobStatus); // Use general UpdateAsync

                // 2. Perform Blob Splitting (Complex Logic)
                var chunkInfos = await SplitBlobIntoChunksAsync(
                                            triggerInfo.ContainerName,
                                            triggerInfo.BlobName,
                                            triggerInfo.OriginalUploadCorrelationId,
                                            cancellationToken);

                if (!chunkInfos.Any())
                {
                    _logger.LogError("Chunking process for {BlobName} resulted in no chunks. Marking master job as failed.", triggerInfo.BlobName);
                    await _jobStatusRepository.UpdateJobStatusAsync(triggerInfo.OriginalUploadCorrelationId, ProcessingStatus.Failed, "Chunking failed to produce any chunks.", null);
                    return;
                }

                // 3. Create JobStatus for each chunk and Enqueue Hangfire jobs
                int totalChunksCreated = 0;
                foreach (var chunkInfo in chunkInfos)
                {
                    var isChunkAlreadyQueued = await _jobStatusRepository.CheckJobAlreadyQueuedAsync(chunkInfo.ChunkBlobName, JobType.Chunk, cancellationToken);
                    if (isChunkAlreadyQueued)
                        continue; // Skip if chunk already exists in the repository

                    cancellationToken.ThrowIfCancellationRequested();
                    var chunkJobStatus = new JobStatus
                    {
                        CorrelationId = chunkInfo.ChunkCorrelationId,
                        ParentCorrelationId = triggerInfo.OriginalUploadCorrelationId,
                        Type = JobType.Chunk,
                        Status = ProcessingStatus.QueuedForProcessing,
                        Message = $"Chunk {chunkInfo.ChunkNumber} of {chunkInfos.Count} queued for processing.",
                        OriginalFileName = masterJobStatus.OriginalFileName, // Inherit from master
                        BlobName = chunkInfo.ChunkBlobName,
                        ContainerName = chunkInfo.ChunkContainerName,
                        CreatedAtUtc = DateTime.UtcNow,
                        LastUpdatedAtUtc = DateTime.UtcNow
                    };
                    await _jobStatusRepository.CreateJobStatusAsync(chunkJobStatus);

                    _backgroundJobClient.Enqueue<Hangfire.Jobs.CdrFileProcessingJobs>(
                        job => job.ProcessFileFromBlobAsync(chunkInfo.ChunkContainerName, chunkInfo.ChunkBlobName, chunkInfo.ChunkCorrelationId)
                    );
                    totalChunksCreated++;
                }

                // 4. Update Master Job Status to ChunksQueued
                masterJobStatus.Status = ProcessingStatus.ChunksQueued;
                masterJobStatus.Message = $"{totalChunksCreated} chunks created and queued for processing.";
                masterJobStatus.TotalChunks = totalChunksCreated;
                masterJobStatus.ProcessedChunks = 0;
                masterJobStatus.SuccessfulChunks = 0;
                masterJobStatus.FailedChunks = 0;
                await _jobStatusRepository.UpdateJobStatusAsync(masterJobStatus);

                _logger.LogInformation("Successfully chunked {BlobName} into {TotalChunks} chunks and enqueued processing jobs.",
                    triggerInfo.BlobName, totalChunksCreated);

                // Optionally delete the original large blob after successful chunking
                // await _blobStorageService.DeleteFileAsync(triggerInfo.ContainerName, triggerInfo.BlobName);
            }
            else // File is small enough or is already a chunk, process as a single unit
            {
                if (masterJobStatus.Type == JobType.Chunk)
                {
                    _logger.LogInformation("Blob {BlobName} is a chunk. Enqueuing for direct processing with CorrelationId {CorrelationId}.",
                       triggerInfo.BlobName, triggerInfo.OriginalUploadCorrelationId); // OriginalUploadCorrelationId is the chunk's ID here
                }
                else
                {
                    _logger.LogInformation("Blob {BlobName} does not require chunking. Enqueuing as single file job with CorrelationId {CorrelationId}.",
                        triggerInfo.BlobName, triggerInfo.OriginalUploadCorrelationId);
                    masterJobStatus.Type = JobType.SingleFile; // Ensure type is SingleFile if not chunked
                    masterJobStatus.Status = ProcessingStatus.QueuedForProcessing;
                    masterJobStatus.Message = "File queued for processing as a single unit.";
                    await _jobStatusRepository.UpdateJobStatusAsync(masterJobStatus);
                }

                _backgroundJobClient.Enqueue<Hangfire.Jobs.CdrFileProcessingJobs>(
                    job => job.ProcessFileFromBlobAsync(triggerInfo.ContainerName, triggerInfo.BlobName, triggerInfo.OriginalUploadCorrelationId)
                );

                _logger.LogInformation("Enqueued single processing job for blob: {BlobName}, CorrelationId: {CorrelationId}",
                    triggerInfo.BlobName, triggerInfo.OriginalUploadCorrelationId);
            }
        }

        // Placeholder for the complex chunking logic
        private async Task<List<ChunkInfo>> SplitBlobIntoChunksAsync(
            string containerName, string originalBlobName, Guid masterCorrelationId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting to split blob {OriginalBlobName} in container {ContainerName}.", originalBlobName, containerName);
            var chunkInfos = new List<ChunkInfo>();
            int chunkNumber = 1;

            try
            {
                using var originalBlobStream = await _blobStorageService.DownloadFileAsync(containerName, originalBlobName);
                if (originalBlobStream == null || originalBlobStream == Stream.Null)
                {
                    _logger.LogError("Original blob {OriginalBlobName} could not be downloaded for chunking.", originalBlobName);
                    return chunkInfos; // Return empty list
                }

                using var streamReader = new StreamReader(originalBlobStream, Encoding.UTF8, true);
                string? headerLine = null;
                if (streamReader.Peek() >= 0) // Check if stream is not empty
                {
                    headerLine = await streamReader.ReadLineAsync(cancellationToken);
                }

                if (string.IsNullOrEmpty(headerLine))
                {
                    _logger.LogWarning("Blob {OriginalBlobName} is empty or header could not be read. No chunks created.", originalBlobName);
                    return chunkInfos;
                }

                while (streamReader.Peek() >= 0) // While there's more data
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentChunkLines = new List<string> { headerLine }; // Each chunk gets the header
                    long currentChunkSize = Encoding.UTF8.GetByteCount(headerLine + Environment.NewLine);

                    string? line;
                    while ((line = await streamReader.ReadLineAsync(cancellationToken)) != null)
                    {
                        currentChunkLines.Add(line);
                        currentChunkSize += Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                        if (currentChunkSize >= _targetChunkSizeBytes)
                        {
                            break; // Current chunk is large enough
                        }
                    }

                    if (currentChunkLines.Count > 1) // More than just the header
                    {
                        string chunkContent = string.Join(Environment.NewLine, currentChunkLines);
                        using var chunkStream = new MemoryStream(Encoding.UTF8.GetBytes(chunkContent));

                        var chunkBlobName = $"{Path.GetFileNameWithoutExtension(originalBlobName)}_chunk{chunkNumber}{Path.GetExtension(originalBlobName)}";
                        var chunkCorrelationId = Guid.NewGuid(); // Unique ID for this chunk's job status

                        // Upload the chunk
                        await _blobStorageService.UploadFileAsync(
                            containerName, // Or a dedicated chunks container
                            chunkBlobName,
                            chunkStream,
                            metadata: new Dictionary<string, string> {
                                { "ParentCorrelationId", masterCorrelationId.ToString() },
                                { "UploadCorrelationId", chunkCorrelationId.ToString() },
                                { "ChunkNumber", chunkNumber.ToString() },
                                { "IsChunk", "true" }
                            });

                        chunkInfos.Add(new ChunkInfo
                        {
                            ChunkBlobName = chunkBlobName,
                            ChunkContainerName = containerName, // Or chunks container
                            ChunkCorrelationId = chunkCorrelationId,
                            ChunkNumber = chunkNumber
                        });
                        _logger.LogInformation("Created and uploaded chunk {ChunkNumber}: {ChunkBlobName} for master {MasterCorrelationId} with correlationId {ChunkCorrelationId}", chunkNumber, chunkBlobName, masterCorrelationId, chunkCorrelationId);
                        chunkNumber++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Blob splitting was cancelled for {OriginalBlobName}.", originalBlobName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during blob splitting for {OriginalBlobName}.", originalBlobName);
                // Depending on policy, might clear chunkInfos or rethrow to fail the master job
                chunkInfos.Clear(); // Don't process partial chunks if splitting fails catastrophically
                throw; // Rethrow to let the orchestrator handle this failure
            }
            return chunkInfos;
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
