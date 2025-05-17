
using TelecomCdr.Domain;

namespace TelecomCdr.Abstraction.Interfaces.Repository
{
    public interface IFailedCdrRecordRepository
    {
        Task AddAsync(FailedCdrRecord failedRecord, CancellationToken cancellationToken = default);
        Task AddBatchAsync(IEnumerable<FailedCdrRecord> failedRecords, CancellationToken cancellationToken = default);
        Task AddBatchAsync(IEnumerable<FailedCdrRecord> failedRecords);
        Task<IEnumerable<FailedCdrRecord>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default);
    }
}
