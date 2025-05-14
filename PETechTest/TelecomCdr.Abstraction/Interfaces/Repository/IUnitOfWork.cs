namespace TelecomCdr.Abstraction.Interfaces.Repository
{
    public interface IUnitOfWork : IDisposable
    {
        ICdrRepository CdrRepository { get; }
        IJobStatusRepository JobStatusRepository { get; }
        IFailedCdrRecordRepository FailedCdrRecordRepository { get; }

        Task<int> CommitAsync(CancellationToken cancellationToken = default);
    }
}
