using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.DurableFunctions.Activities;
using TelecomCdr.DurableFunctions.Dtos;

public class CsvProcessingOrchestratorFunction
{
    // Define constants for thresholds, etc. (or get from config)
    private const long CHUNK_THRESHOLD_BYTES = 500 * 1024 * 1024; // 500MB

    [Function(nameof(CsvProcessingOrchestratorFunction))]
    public async Task<OrchestrationResult> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(CsvProcessingOrchestratorFunction));
        var input = context.GetInput<OrchestratorInput>(); // Define OrchestratorInput class
        logger.LogInformation("Orchestration started for MasterCorrelationId: {MasterCorrelationId}, Blob: {BlobName}",
            input.MasterCorrelationId, input.BlobName);

        var outputs = new List<FileProcessingResult>();
        var masterJobStatus = new OrchestrationResult { MasterCorrelationId = input.MasterCorrelationId };

        try
        {
            // 1. Update Master Job Status to Processing
            await context.CallActivityAsync(nameof(UpdateJobStatusActivityFunction), // New activity for status updates
                new JobStatusActivityInput { CorrelationId = input.MasterCorrelationId, Status = ProcessingStatus.Processing, Message = "Orchestration started." });

            // 2. Get Blob Metadata (Size)
            var blobMetadata = await context.CallActivityAsync<BlobMetadataResult>(nameof(GetBlobMetadataActivityFunction),
                new BlobMetadataInput { ContainerName = input.ContainerName, BlobName = input.BlobName });

            if (blobMetadata == null || blobMetadata.Size == 0)
            {
                logger.LogError("Failed to get blob metadata or blob is empty for {BlobName}.", input.BlobName);
                await context.CallActivityAsync(nameof(UpdateJobStatusActivityFunction),
                    new JobStatusActivityInput { CorrelationId = input.MasterCorrelationId, Status = ProcessingStatus.Failed, Message = "Blob empty or metadata read failed." });
                masterJobStatus.OverallStatus = ProcessingStatus.Failed;
                return masterJobStatus;
            }
            masterJobStatus.OriginalBlobSize = blobMetadata.Size;

            // 3. Chunking Decision
            List<ChunkInfo> chunksToProcess = new List<ChunkInfo>();
            if (blobMetadata.Size > CHUNK_THRESHOLD_BYTES)
            {
                logger.LogInformation("Blob {BlobName} size {Size} > threshold {Threshold}. Initiating chunking.",
                    input.BlobName, blobMetadata.Size, CHUNK_THRESHOLD_BYTES);
                await context.CallActivityAsync(nameof(UpdateJobStatusActivityFunction),
                    new JobStatusActivityInput { CorrelationId = input.MasterCorrelationId, Status = ProcessingStatus.Chunking, Message = "Splitting file into chunks." });

                chunksToProcess = await context.CallActivityAsync<List<ChunkInfo>>(nameof(SplitBlobIntoChunksActivityFunction),
                    new SplitBlobInput { OriginalContainer = input.ContainerName, OriginalBlobName = input.BlobName, MasterCorrelationId = input.MasterCorrelationId });

                if (chunksToProcess == null || !chunksToProcess.Any())
                {
                    logger.LogError("Chunking failed or produced no chunks for {BlobName}.", input.BlobName);
                    await context.CallActivityAsync(nameof(UpdateJobStatusActivityFunction),
                        new JobStatusActivityInput { CorrelationId = input.MasterCorrelationId, Status = ProcessingStatus.Failed, Message = "Chunking failed." });
                    masterJobStatus.OverallStatus = ProcessingStatus.Failed;
                    return masterJobStatus;
                }

                await context.CallActivityAsync(nameof(UpdateJobStatusActivityFunction),
                    new JobStatusActivityInput
                    {
                        CorrelationId = input.MasterCorrelationId,
                        Status = ProcessingStatus.ChunksQueued,
                        Message = $"{chunksToProcess.Count} chunks queued.",
                        TotalChunks = chunksToProcess.Count,
                        ProcessedChunks = 0,
                        SuccessfulChunks = 0,
                        FailedChunks = 0
                    });
            }
            else // Process as a single file (one "chunk")
            {
                logger.LogInformation("Blob {BlobName} size {Size} <= threshold. Processing as single file.", input.BlobName, blobMetadata.Size);
                // Create a "virtual" chunk for the whole file
                chunksToProcess.Add(new ChunkInfo
                {
                    ChunkBlobName = input.BlobName,
                    ChunkContainerName = input.ContainerName,
                    ChunkCorrelationId = input.MasterCorrelationId, // Use master ID for single file processing status
                    ChunkNumber = 1
                });
                await context.CallActivityAsync(nameof(UpdateJobStatusActivityFunction),
                    new JobStatusActivityInput { CorrelationId = input.MasterCorrelationId, Status = ProcessingStatus.QueuedForProcessing, Message = "File queued for single processing.", Type = JobType.SingleFile });
            }

            // 4. Fan-Out: Process Chunks in Parallel
            var processingTasks = new List<Task<FileProcessingResult>>();
            foreach (var chunk in chunksToProcess)
            {
                // If it was chunked, create individual chunk job statuses
                if (blobMetadata.Size > CHUNK_THRESHOLD_BYTES)
                {
                    await context.CallActivityAsync(nameof(UpdateJobStatusActivityFunction),
                        new JobStatusActivityInput
                        {
                            CorrelationId = chunk.ChunkCorrelationId,
                            ParentCorrelationId = input.MasterCorrelationId,
                            Type = JobType.Chunk,
                            Status = ProcessingStatus.QueuedForProcessing,
                            Message = $"Chunk {chunk.ChunkNumber} queued.",
                            BlobName = chunk.ChunkBlobName,
                            ContainerName = chunk.ChunkContainerName
                        });
                }
                processingTasks.Add(context.CallActivityAsync<FileProcessingResult>(nameof(ProcessChunkActivityFunction), chunk));
            }

            // 5. Fan-In: Wait for all chunks to complete
            var chunkResults = await Task.WhenAll(processingTasks);
            outputs.AddRange(chunkResults);

            // 6. Aggregate Results and Finalize Master Status
            // This logic would now primarily reside in IJobStatusRepository.UpdateMasterJobStatusBasedOnChunksAsync
            // The activity function ProcessChunkActivityFunction would call IncrementProcessedChunkCountAsync
            // which in turn calls UpdateMasterJobStatusBasedOnChunksAsync.
            // The orchestrator just needs to ensure all tasks are complete.
            // For simplicity here, we might just log. The final status is determined by the repository logic.

            logger.LogInformation("All processing activities complete for MasterCorrelationId: {MasterCorrelationId}. Final status will be determined by aggregated chunk results.", input.MasterCorrelationId);
            // The final status of the master job is updated by the repository based on chunk completions.
            // We can fetch the final master status to return it if needed.
            var finalMasterState = await context.CallActivityAsync<JobStatus>(nameof(GetJobStatusActivityFunction), input.MasterCorrelationId);
            masterJobStatus.OverallStatus = finalMasterState?.Status ?? ProcessingStatus.Failed;
            masterJobStatus.FinalMessage = finalMasterState?.Message ?? "Aggregation completed.";
            masterJobStatus.TotalSuccessfulRecords = finalMasterState?.ProcessedRecordsCount ?? 0;
            masterJobStatus.TotalFailedRecords = finalMasterState?.FailedRecordsCount ?? 0;

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Orchestration failed for MasterCorrelationId: {MasterCorrelationId}", input.MasterCorrelationId);
            masterJobStatus.OverallStatus = ProcessingStatus.Failed;
            masterJobStatus.FinalMessage = $"Orchestration failed: {ex.Message}";
            // Attempt to update master job status to failed
            try
            {
                await context.CallActivityAsync(nameof(UpdateJobStatusActivityFunction),
                    new JobStatusActivityInput { CorrelationId = input.MasterCorrelationId, Status = ProcessingStatus.Failed, Message = masterJobStatus.FinalMessage });
            }
            catch (Exception updateEx)
            {
                logger.LogError(updateEx, "Failed to update master job status to Failed after orchestration error for {MasterCorrelationId}", input.MasterCorrelationId);
            }
        }
        logger.LogInformation("Orchestration finished for MasterCorrelationId: {MasterCorrelationId} with status {OverallStatus}", input.MasterCorrelationId, masterJobStatus.OverallStatus);
        return masterJobStatus;
    }
}