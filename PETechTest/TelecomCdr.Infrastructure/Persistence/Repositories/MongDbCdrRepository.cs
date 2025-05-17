
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Domain;

namespace TelecomCdr.Infrastructure.Persistence.Repositories
{
    public class MongDbCdrRepository : ICdrRepository
    {
        public Task AddBatchAsync(IEnumerable<CallDetailRecord> records, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task AddBatchAsync(IEnumerable<CallDetailRecord> records)
        {
            throw new NotImplementedException();
        }

        public Task<(List<CallDetailRecord> Items, int TotalCount)> GetByCallerIdAsync(string callerId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<(List<CallDetailRecord> Items, int TotalCount)> GetByCorrelationIdAsync(Guid correlationId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<(List<CallDetailRecord> Items, int TotalCount)> GetByRecipientIdAsync(string recipientId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<CallDetailRecord?> GetByReferenceAsync(string reference, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
