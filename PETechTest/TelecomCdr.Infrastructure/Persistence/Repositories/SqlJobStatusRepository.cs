using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Domain;

namespace TelecomCdr.Infrastructure.Persistence.Repositories
{
    public class SqlJobStatusRepository : IJobStatusRepository
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SqlJobStatusRepository> _logger;

        public SqlJobStatusRepository(AppDbContext context, ILogger<SqlJobStatusRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task CreateAsync(JobStatus jobStatus)
        {
            if (jobStatus == null) throw new ArgumentNullException(nameof(jobStatus));
            if (jobStatus.CorrelationId == Guid.Empty) throw new ArgumentException("CorrelationId cannot be empty.", nameof(jobStatus.CorrelationId));

            jobStatus.CreatedAtUtc = DateTime.UtcNow;
            jobStatus.LastUpdatedAtUtc = DateTime.UtcNow;

            await _context.JobStatuses.AddAsync(jobStatus);
            await _context.SaveChangesAsync();
            _logger.LogInformation("Created job status for CorrelationId: {CorrelationId}", jobStatus.CorrelationId);
        }

        public async Task<JobStatus?> GetByCorrelationIdAsync(Guid correlationId)
        {
            if (correlationId == Guid.Empty) return null;
            return await _context.JobStatuses.FirstOrDefaultAsync(js => js.CorrelationId == correlationId);
        }

        public async Task UpdateAsync(JobStatus jobStatus)
        {
            if (jobStatus == null) throw new ArgumentNullException(nameof(jobStatus));

            var existingStatus = await _context.JobStatuses.FindAsync(jobStatus.CorrelationId);
            if (existingStatus != null)
            {
                // EF Core tracks changes, but explicitly setting fields is safer
                // and allows for partial updates if the input 'jobStatus' is not fully populated.
                // However, for this example, we assume 'jobStatus' is the complete updated entity.
                _context.Entry(existingStatus).CurrentValues.SetValues(jobStatus); // Efficiently updates all changed properties
                existingStatus.LastUpdatedAtUtc = DateTime.UtcNow; // Ensure LastUpdatedAtUtc is always updated
                _context.JobStatuses.Update(existingStatus); // Mark as modified
                await _context.SaveChangesAsync();
                _logger.LogInformation("Updated job status for CorrelationId: {CorrelationId} to Status: {Status}", jobStatus.CorrelationId, jobStatus.Status);
            }
            else
            {
                _logger.LogWarning("Attempted to update non-existent job status for CorrelationId: {CorrelationId}", jobStatus.CorrelationId);
                throw new InvalidOperationException($"JobStatus with CorrelationId {jobStatus.CorrelationId} not found for update.");
            }
        }

        public async Task UpdateStatusAsync(Guid correlationId, ProcessingStatus status, string? message = null, int? processedCount = null, int? failedCount = null)
        {
            var jobStatus = await GetByCorrelationIdAsync(correlationId);
            if (jobStatus != null)
            {
                jobStatus.Status = status;
                jobStatus.Message = message ?? jobStatus.Message; // Keep old message if new one is null
                jobStatus.ProcessedRecordsCount = processedCount ?? jobStatus.ProcessedRecordsCount;
                jobStatus.FailedRecordsCount = failedCount ?? jobStatus.FailedRecordsCount;
                jobStatus.LastUpdatedAtUtc = DateTime.UtcNow;

                _context.JobStatuses.Update(jobStatus);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Updated status for CorrelationId: {CorrelationId} to: {Status}, Message: {Message}", correlationId, status, message);
            }
            else
            {
                _logger.LogWarning("Job status not found for CorrelationId: {CorrelationId} during status update to {Status}.", correlationId, status);
                // Depending on requirements, you might throw an exception or just log.
                // Throwing helps identify issues if a status record is expected.
                throw new InvalidOperationException($"JobStatus with CorrelationId {correlationId} not found for status update.");
            }
        }
    }
}
