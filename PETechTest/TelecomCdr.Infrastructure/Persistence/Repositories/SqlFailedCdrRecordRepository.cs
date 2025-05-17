using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Domain;

namespace TelecomCdr.Infrastructure.Persistence.Repositories
{
    public class SqlFailedCdrRecordRepository : IFailedCdrRecordRepository
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SqlFailedCdrRecordRepository> _logger;

        public SqlFailedCdrRecordRepository(AppDbContext context, ILogger<SqlFailedCdrRecordRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AddAsync(FailedCdrRecord failedRecord, CancellationToken cancellationToken = default)
        {
            if (failedRecord == null) throw new ArgumentNullException(nameof(failedRecord));

            failedRecord.FailedAtUtc = DateTime.UtcNow;
            await _context.FailedCdrRecords.AddAsync(failedRecord, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Saved failed CDR record for CorrelationId: {CorrelationId}, RawData: {RawData}, Error: {Error}",
                failedRecord.UploadCorrelationId, failedRecord.RawRowData, failedRecord.ErrorMessage);
        }

        public async Task AddBatchAsync(IEnumerable<FailedCdrRecord> failedRecords, CancellationToken cancellationToken = default)
        {
            if (failedRecords == null || !failedRecords.Any()) return;

            var utcNow = DateTime.UtcNow;
            foreach (var record in failedRecords)
            {
                record.FailedAtUtc = utcNow;
            }

            await _context.FailedCdrRecords.AddRangeAsync(failedRecords, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Saved a batch of {Count} failed CDR records for CorrelationId: {CorrelationId}",
                failedRecords.Count(), failedRecords.FirstOrDefault()?.UploadCorrelationId);
        }

        public async Task AddBatchAsync(IEnumerable<FailedCdrRecord> failedRecords)
        {
            if (failedRecords == null || !failedRecords.Any()) return;

            var utcNow = DateTime.UtcNow;
            foreach (var record in failedRecords)
            {
                record.FailedAtUtc = utcNow;
            }

            await _context.FailedCdrRecords.AddRangeAsync(failedRecords);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Saved a batch of {Count} failed CDR records for CorrelationId: {CorrelationId}",
                failedRecords.Count(), failedRecords.FirstOrDefault()?.UploadCorrelationId);
        }

        public async Task<IEnumerable<FailedCdrRecord>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                return Enumerable.Empty<FailedCdrRecord>();
            }

            return await _context.FailedCdrRecords
                .Where(fr => fr.UploadCorrelationId == correlationId)
                .OrderBy(fr => fr.RowNumberInCsv ?? fr.Id) // Order by row number if available, otherwise by ID
                .ToListAsync(cancellationToken);
        }
    }
}
