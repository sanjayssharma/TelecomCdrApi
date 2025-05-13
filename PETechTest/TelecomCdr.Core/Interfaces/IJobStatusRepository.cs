using TelecomCdr.Core.Models.DomainModel;

namespace TelecomCdr.Core.Interfaces
{
    public interface IJobStatusRepository
    {
        Task CreateAsync(JobStatus jobStatus);
        Task<JobStatus?> GetByCorrelationIdAsync(Guid correlationId);
        Task UpdateAsync(JobStatus jobStatus);
        // Potentially add a method to update status and message specifically
        Task UpdateStatusAsync(Guid correlationId, ProcessingStatus status, string? message = null, int? processedCount = null, int? failedCount = null);
    }
}
