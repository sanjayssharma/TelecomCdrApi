using TelecomCdr.Domain;

namespace TelecomCdr.Abstraction.Interfaces.Repository
{
    public interface IJobStatusRepository
    {
        Task CreateJobStatusAsync(JobStatus jobStatus);
        Task<JobStatus?> GetJobStatusByCorrelationIdAsync(Guid correlationId);
        Task UpdateJobStatusAsync(JobStatus jobStatus);
        Task UpdateJobStatusAsync(Guid correlationId, ProcessingStatus status, string errorMessage = null, int? totalChunks = null);
        Task UpdateJobStatusAsync(Guid correlationId, ProcessingStatus status, string? message = null, int? processedRecords = null, int? failedRecords = null);
        Task<bool> CheckJobAlreadyQueuedAsync(string blobName, JobType jobType, CancellationToken cancellationToken = default);

        // Chunk management
        // This method now needs to handle aggregation for master job
        Task IncrementProcessedChunkCountAsync(Guid parentCorrelationId, bool chunkSucceeded, long chunkProcessedRecords, long chunkFailedRecords);
        Task IncrementProcessedChunkCountAsync(Guid? parentCorrelationId, bool chunkSucceeded);
        Task<IEnumerable<JobStatus>> GetChunkStatusesAsync(Guid parentCorrelationId);
        Task UpdateMasterJobStatusBasedOnChunksAsync(Guid parentCorrelationId);
        // A more specific update method might be useful
        Task UpdateJobStatusProcessingResultAsync(Guid correlationId, ProcessingStatus status, long processedRecords, long failedRecords, string errorMessage = null);
    }
}
