
using TelecomCdr.Domain;

namespace TelecomCdr.Abstraction.Interfaces.Repository
{
    public interface IFailedCdrRecordRepository
    {
        Task AddAsync(FailedCdrRecord failedRecord);
        Task AddBatchAsync(IEnumerable<FailedCdrRecord> failedRecords);
        Task<IEnumerable<FailedCdrRecord>> GetByCorrelationIdAsync(string correlationId);
    }
}
