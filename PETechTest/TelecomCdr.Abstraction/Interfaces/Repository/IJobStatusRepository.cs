using TelecomCdr.Domain;

namespace TelecomCdr.Abstraction.Interfaces.Repository
{
    public interface IJobStatusRepository
    {
        Task CreateJobStatusAsync(JobStatus jobStatus);
        Task<JobStatus?> GetJobStatusByCorrelationIdAsync(Guid correlationId);
        Task UpdateJobStatusAsync(JobStatus jobStatus);
        Task UpdateJobStatusAsync(Guid correlationId, ProcessingStatus status, string? message = null, int? processedRecords = null, int? failedRecords = null);

        // Chunk management
        // This method now needs to handle aggregation for master job
        Task IncrementProcessedChunkCountAsync(Guid parentCorrelationId, bool chunkSucceeded, long chunkProcessedRecords, long chunkFailedRecords);
        Task<IEnumerable<JobStatus>> GetChunkStatusesAsync(Guid parentCorrelationId);
        Task UpdateMasterJobStatusBasedOnChunksAsync(Guid parentCorrelationId);
        Task UpdateJobStatusAsync(Guid correlationId, ProcessingStatus status, string errorMessage = null, int? totalChunks = null);
        // A more specific update method might be useful
        Task UpdateJobStatusProcessingResultAsync(Guid correlationId, ProcessingStatus status, long processedRecords, long failedRecords, string errorMessage = null);
    }
}
