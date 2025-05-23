﻿using TelecomCdr.Domain;

namespace TelecomCdr.Abstraction.Interfaces.Repository
{
    public interface ICdrRepository
    {
        Task AddBatchAsync(IEnumerable<CallDetailRecord> records, CancellationToken cancellationToken = default);
        Task AddBatchAsync(IEnumerable<CallDetailRecord> records);
        Task<CallDetailRecord?> GetByReferenceAsync(string reference, CancellationToken cancellationToken = default);
        Task<(List<CallDetailRecord> Items, int TotalCount)> GetByCallerIdAsync(string callerId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
        Task<(List<CallDetailRecord> Items, int TotalCount)> GetByRecipientIdAsync(string recipientId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
        Task<(List<CallDetailRecord> Items, int TotalCount)> GetByCorrelationIdAsync(Guid correlationId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    }
}
