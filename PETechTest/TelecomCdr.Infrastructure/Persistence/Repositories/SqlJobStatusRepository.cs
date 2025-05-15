using Hangfire.Common;
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

        public async Task CreateJobStatusAsync(JobStatus jobStatus)
        {
            if (jobStatus == null) throw new ArgumentNullException(nameof(jobStatus));
            if (jobStatus.CorrelationId == Guid.Empty) throw new ArgumentException("CorrelationId cannot be empty.", nameof(jobStatus.CorrelationId));

            try
            {
                jobStatus.CreatedAtUtc = DateTime.UtcNow;
                jobStatus.LastUpdatedAtUtc = DateTime.UtcNow;

                await _context.JobStatuses.AddAsync(jobStatus);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Created job status for CorrelationId: {CorrelationId}", jobStatus.CorrelationId);
            }
            catch (DbUpdateException ex) // Catches errors during SaveChanges, like PK violation
            {
                _logger?.LogError(ex, $"Database error creating JobStatus for CorrelationId: {jobStatus.CorrelationId}");
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Generic error creating JobStatus for CorrelationId: {jobStatus.CorrelationId}");
                throw;
            }
        }

        public async Task<JobStatus?> GetJobStatusByCorrelationIdAsync(Guid correlationId)
        {
            if (correlationId == Guid.Empty)
                throw new ArgumentException("CorrelationId cannot be null or empty.", nameof(correlationId));

            try
            {
                // AsNoTracking() can be used if the entity is read-only and not going to be updated in the same context.
                // However, since other methods might update it, we'll track it by default.
                return await _context.JobStatuses.FirstOrDefaultAsync(js => js.CorrelationId == correlationId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Error retrieving JobStatus for CorrelationId: {correlationId}");
                throw; // Re-throw to allow higher-level handling
            }
        }

        public async Task UpdateJobStatusAsync(JobStatus jobStatus)
        {
            if (jobStatus == null) throw new ArgumentNullException(nameof(jobStatus));
            if (jobStatus.CorrelationId == Guid.Empty) throw new ArgumentException("CorrelationId cannot be empty.", nameof(jobStatus.CorrelationId));

            try
            {
                // This handles cases where the jobStatus object might have been created outside the current context.
                var existingEntry = _context.JobStatuses.Local.FirstOrDefault(e => e.CorrelationId == jobStatus.CorrelationId);
                if (existingEntry == null)
                {
                    _context.JobStatuses.Attach(jobStatus);
                    _context.Entry(jobStatus).State = EntityState.Modified;
                }
                else // If already tracked, ensure its state is Modified if changes were made outside EF's direct tracking
                {
                    _context.Entry(existingEntry).CurrentValues.SetValues(jobStatus); // Apply changes from the passed object
                    _context.Entry(existingEntry).State = EntityState.Modified;
                }


                jobStatus.LastUpdatedAtUtc = DateTime.UtcNow;
                // If jobStatus was attached, UpdatedAt on the attached entity needs to be set.
                _context.Entry(jobStatus).Property(x => x.LastUpdatedAtUtc).CurrentValue = jobStatus.LastUpdatedAtUtc;


                await _context.SaveChangesAsync();
                _logger?.LogInformation("JobStatus updated for CorrelationId: {CorrelationId}, New Status: {JobStatus}", jobStatus.CorrelationId, jobStatus.Status);
            }
            catch (DbUpdateConcurrencyException ex) // Optimistic concurrency issues
            {
                _logger?.LogError(ex, $"Concurrency error updating JobStatus for CorrelationId: {jobStatus.CorrelationId}. The record might have been modified or deleted.");
                // Handle concurrency: e.g., reload the entity and retry, or inform the user.
                throw new KeyNotFoundException($"JobStatus with CorrelationId {jobStatus.CorrelationId} was modified or not found during update.", ex);
            }
            catch (DbUpdateException ex)
            {
                _logger?.LogError(ex, $"Database error updating JobStatus for CorrelationId: {jobStatus.CorrelationId}");
                // Check if it's because the entity doesn't exist
                var exists = await _context.JobStatuses.AnyAsync(e => e.CorrelationId == jobStatus.CorrelationId);
                if (!exists)
                {
                    throw new KeyNotFoundException($"JobStatus with CorrelationId {jobStatus.CorrelationId} not found.", ex);
                }
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Generic error updating JobStatus for CorrelationId: {jobStatus.CorrelationId}");
                throw;
            }
        }

        public async Task UpdateJobStatusAsync(Guid correlationId, ProcessingStatus status, string? message = null, int? processedRecords = null, int? failedRecords = null)
        {
            var jobStatus = await GetJobStatusByCorrelationIdAsync(correlationId);
            if (jobStatus != null)
            {
                jobStatus.Status = status;
                jobStatus.Message = message ?? jobStatus.Message;
                if (jobStatus.Type != JobType.Master) // Only update record counts for chunks or single files
                {
                    jobStatus.ProcessedRecordsCount = processedRecords ?? jobStatus.ProcessedRecordsCount;
                    jobStatus.FailedRecordsCount = failedRecords ?? jobStatus.FailedRecordsCount;
                }
                jobStatus.LastUpdatedAtUtc = DateTime.UtcNow;
                _context.JobStatuses.Update(jobStatus);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Updated status for {JobType} Job {CorrelationId} to: {Status}", jobStatus.Type, correlationId, status);
            }
            else
            {
                _logger.LogWarning("Job status not found for CorrelationId: {CorrelationId} during status update to {Status}.", correlationId, status);
                // Depending on requirements, you might throw an exception or just log.
                // Throwing helps identify issues if a status record is expected.
                throw new InvalidOperationException($"JobStatus with CorrelationId {correlationId} not found for status update.");
            }
        }

        public async Task UpdateJobStatusAsync(Guid correlationId, ProcessingStatus status, string errorMessage = null, int? totalChunks = null)
        {
            var jobStatus = await GetJobStatusByCorrelationIdAsync(correlationId);
            if (jobStatus != null)
            {
                jobStatus.Status = status;
                if (errorMessage != null || status == ProcessingStatus.Failed || status == ProcessingStatus.PartiallySucceeded)
                {
                    jobStatus.Message = errorMessage;
                }
                else if (status == ProcessingStatus.Succeeded)
                {
                    jobStatus.Message = null; // Clear error on success
                }

                if (totalChunks.HasValue)
                {
                    jobStatus.TotalChunks = totalChunks;
                }
                // UpdatedAt will be set by UpdateJobStatusAsync
                await UpdateJobStatusAsync(jobStatus);
            }
            else
            {
                _logger?.LogWarning($"JobStatus not found for CorrelationId: {correlationId} during UpdateStatusAsync. Status not updated.");
                throw new KeyNotFoundException($"JobStatus with CorrelationId {correlationId} not found for status update.");
            }
        }

        // Increments chunk counts on the master job status
        public async Task IncrementProcessedChunkCountAsync(Guid parentCorrelationId, bool chunkSucceeded, long chunkProcessedRecords, long chunkFailedRecords)
        {
            if (parentCorrelationId == Guid.Empty)
            {
                _logger?.LogError("ParentCorrelationId is null or empty in IncrementProcessedChunkCountAsync.");
                throw new ArgumentNullException(nameof(parentCorrelationId));
            }

            // Use a transaction to ensure atomicity of read-modify-write operations
            using (var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable)) // Or RepeatableRead
            {
                try
                {
                    var masterJobStatus = await _context.JobStatuses
                                                .FirstOrDefaultAsync(js => js.CorrelationId == parentCorrelationId);

                    if (masterJobStatus == null || masterJobStatus.Type != JobType.Master)
                    {
                        _logger?.LogError($"Master JobStatus not found or not of Type Master for ParentCorrelationId: {parentCorrelationId}. Cannot increment chunk count.");
                        await transaction.RollbackAsync(); // Rollback before throwing or returning
                        throw new KeyNotFoundException($"Master JobStatus with CorrelationId {parentCorrelationId} not found or not of type Master.");
                    }

                    masterJobStatus.ProcessedChunks = (masterJobStatus.ProcessedChunks ?? 0) + 1;
                    masterJobStatus.ProcessedRecordsCount = (masterJobStatus.ProcessedRecordsCount ?? 0) + chunkProcessedRecords;
                    masterJobStatus.FailedRecordsCount = (masterJobStatus.FailedRecordsCount ?? 0) + chunkFailedRecords;

                    if (chunkSucceeded)
                    {
                        masterJobStatus.SuccessfulChunks = (masterJobStatus.SuccessfulChunks ?? 0) + 1;
                    }
                    else
                    {
                        masterJobStatus.FailedChunks = (masterJobStatus.FailedChunks ?? 0) + 1;
                    }

                    masterJobStatus.LastUpdatedAtUtc = DateTime.UtcNow;

                    if (masterJobStatus.ProcessedChunks >= masterJobStatus.TotalChunks)
                    {
                        _logger?.LogInformation($"All {masterJobStatus.TotalChunks} chunks processed for master job {parentCorrelationId}. Determining final status.");
                        if (masterJobStatus.FailedChunks > 0)
                        {
                            masterJobStatus.Status = (masterJobStatus.SuccessfulChunks > 0) ? ProcessingStatus.PartiallySucceeded : ProcessingStatus.Failed;
                            masterJobStatus.Message = $"{masterJobStatus.FailedChunks} out of {masterJobStatus.TotalChunks} chunks failed. " +
                                                           $"{(masterJobStatus.SuccessfulChunks > 0 ? "Some data may not have been processed." : "All processed chunks failed.")}";
                        }
                        else
                        {
                            masterJobStatus.Status = ProcessingStatus.Succeeded;
                            masterJobStatus.Message = null;
                        }
                        _logger?.LogInformation($"Master job {parentCorrelationId} final status: {masterJobStatus.Status}. Successful: {masterJobStatus.SuccessfulChunks}, Failed: {masterJobStatus.FailedChunks}.");
                    }
                    else
                    {
                        _logger?.LogInformation($"{masterJobStatus.ProcessedChunks}/{masterJobStatus.TotalChunks} chunks processed for master job {parentCorrelationId}. Status remains {masterJobStatus.Status}.");
                    }

                    _context.JobStatuses.Update(masterJobStatus); // Mark as modified
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Error during IncrementProcessedChunkCountAsync for ParentCorrelationId: {parentCorrelationId}. Transaction rolled back.");
                    await transaction.RollbackAsync();
                    throw; // Re-throw the exception after rolling back
                }
            }
        }

        // Retrieves all chunk statuses for a given parent
        public async Task<IEnumerable<JobStatus>> GetChunkStatusesAsync(Guid parentCorrelationId)
        {
            return await _context.JobStatuses
                .Where(js => js.ParentCorrelationId == parentCorrelationId && js.Type == JobType.Chunk)
                .ToListAsync();
        }

        // Updates the master job's status based on the completion of its chunks
        public async Task UpdateMasterJobStatusBasedOnChunksAsync(Guid parentCorrelationId)
        {
            var masterJob = await GetJobStatusByCorrelationIdAsync(parentCorrelationId);
            if (masterJob == null || masterJob.Type != JobType.Master || !masterJob.TotalChunks.HasValue)
            {
                _logger.LogWarning("Cannot update master job status for {ParentCorrelationId}: Not a master job or total chunks not set.", parentCorrelationId);
                return;
            }

            if (masterJob.ProcessedChunks.HasValue && masterJob.ProcessedChunks >= masterJob.TotalChunks.Value)
            {
                // All chunks have been processed
                long totalProcessedRecords = 0;
                long totalFailedRecords = 0;
                var chunkStatuses = await GetChunkStatusesAsync(parentCorrelationId);

                foreach (var chunk in chunkStatuses)
                {
                    totalProcessedRecords += chunk.ProcessedRecordsCount ?? 0;
                    totalFailedRecords += chunk.FailedRecordsCount ?? 0;
                }
                masterJob.ProcessedRecordsCount = (int)totalProcessedRecords; // Sum from chunks
                masterJob.FailedRecordsCount = (int)totalFailedRecords;     // Sum from chunks

                if (masterJob.FailedChunks.HasValue && masterJob.FailedChunks > 0)
                {
                    masterJob.Status = (masterJob.SuccessfulChunks ?? 0) > 0 ? ProcessingStatus.PartiallySucceeded : ProcessingStatus.Failed;
                    masterJob.Message = $"All {masterJob.TotalChunks} chunks processed. {masterJob.SuccessfulChunks ?? 0} succeeded, {masterJob.FailedChunks ?? 0} failed.";
                }
                else
                {
                    masterJob.Status = ProcessingStatus.Succeeded;
                    masterJob.Message = $"All {masterJob.TotalChunks} chunks processed successfully.";
                }
                masterJob.LastUpdatedAtUtc = DateTime.UtcNow;
                _context.JobStatuses.Update(masterJob);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Master job {ParentCorrelationId} status updated to {Status} based on chunk completion.", parentCorrelationId, masterJob.Status);
            }
        }

        public async Task UpdateJobStatusProcessingResultAsync(Guid correlationId, ProcessingStatus status, long processedRecords, long failedRecords, string errorMessage = null)
        {
            var jobStatus = await GetJobStatusByCorrelationIdAsync(correlationId);
            if (jobStatus != null)
            {
                jobStatus.Status = status;
                jobStatus.ProcessedRecordsCount = processedRecords;
                jobStatus.FailedRecordsCount = failedRecords;

                if (errorMessage != null || status == ProcessingStatus.Failed)
                {
                    jobStatus.Message = errorMessage;
                }
                else if (status == ProcessingStatus.Succeeded)
                {
                    jobStatus.Message = null; // Clear error on success
                }
                // UpdatedAt will be set by UpdateJobStatusAsync
                await UpdateJobStatusAsync(jobStatus);
                _logger?.LogInformation($"JobStatus processing result updated for {correlationId}. Status: {status}, Processed: {processedRecords}, Failed: {failedRecords}.");
            }
            else
            {
                _logger?.LogWarning($"JobStatus not found for CorrelationId: {correlationId} during UpdateJobStatusProcessingResultAsync. Result not updated.");
                throw new KeyNotFoundException($"JobStatus with CorrelationId {correlationId} not found for processing result update.");
            }
        }
    }
}
