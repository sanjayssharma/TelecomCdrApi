using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TelecomCdr.Core.Interfaces;
using TelecomCdr.Core.Models.DomainModels;

namespace TelecomCdr.Core.Infrastructure.Persistence
{
    public class SqlCdrRepository : ICdrRepository
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SqlCdrRepository> _logger;

        public SqlCdrRepository(AppDbContext context, ILogger<SqlCdrRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AddBatchAsync(IEnumerable<CallDetailRecord> records, CancellationToken cancellationToken = default)
        {
            if (records == null || !records.Any())
            {
                _logger.LogWarning("AddBatchAsync called with no records.");
                return;
            }

            try
            {
                await _context.CallDetailRecords.AddRangeAsync(records);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding batch of CDR records.");
                // Handle or rethrow exception as appropriate
                throw;
            }
        }

        public async Task<CallDetailRecord?> GetByReferenceAsync(string reference, CancellationToken cancellationToken = default)
        {
            return await _context.CallDetailRecords
                .FirstOrDefaultAsync(cdr => cdr.Reference == reference, cancellationToken);
        }

        public async Task<(List<CallDetailRecord> Items, int TotalCount)> GetByCallerIdAsync(string callerId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            var query = _context.CallDetailRecords
                .Where(cdr => cdr.CallerId == callerId)
                .OrderByDescending(cdr => cdr.CallEndDateTime); // Example ordering

            var totalCount = await query.CountAsync(); // Get total count matching the filter

            var items = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(); // Get the items for the current page

            return (items, totalCount);
        }

        public async Task<(List<CallDetailRecord> Items, int TotalCount)> GetByRecipientIdAsync(string recipientId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            var query = _context.CallDetailRecords
                .Where(cdr => cdr.Recipient == recipientId)
                .OrderByDescending(cdr => cdr.CallEndDateTime);

            var totalCount = await query.CountAsync();

            var items = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (items, totalCount);
        }

        public async Task<(List<CallDetailRecord> Items, int TotalCount)> GetByCorrelationIdAsync(Guid correlationId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            var query = _context.CallDetailRecords
               .Where(cdr => cdr.UploadCorrelationId == correlationId)
               .OrderByDescending(cdr => cdr.CallEndDateTime);

            var totalCount = await query.CountAsync();

            var items = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (items, totalCount);
        }
    }
}
