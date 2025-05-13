namespace TelecomCdr.Core.Interfaces
{
    public interface IUnitOfWork : IDisposable
    {
        ICdrRepository CdrRepository { get; }
        Task<int> CommitAsync(CancellationToken cancellationToken = default);
    }
}
