using MediatR;
using Microsoft.Extensions.Logging;
using TelecomCdr.Domain;
using TelecomCdr.Core.Models;
using TelecomCdr.Abstraction.Interfaces.Repository;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrsByUploadCorrelationIdQueryHandler : IRequestHandler<GetCdrsByUploadCorrelationIdQuery, PagedResponse<CallDetailRecord>>
    {
        private readonly ICdrRepository _cdrRepository;
        private readonly ILogger<GetCdrsByUploadCorrelationIdQueryHandler> _logger;

        public GetCdrsByUploadCorrelationIdQueryHandler(ICdrRepository cdrRepository, ILogger<GetCdrsByUploadCorrelationIdQueryHandler> logger)
        {
            _cdrRepository = cdrRepository;
            _logger = logger;
        }

        public async Task<PagedResponse<CallDetailRecord>> Handle(GetCdrsByUploadCorrelationIdQuery request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Fetching CDRs by Upload Correlation ID: {UploadCorrelationId}", request.CorrelationId);

            var (items, totalCount) = await _cdrRepository.GetByCorrelationIdAsync(
                request.CorrelationId,
                request.PageNumber,
                request.PageSize);

            // Use the factory method that takes pre-fetched items and count
            return PagedResponse<CallDetailRecord>.Create(items, totalCount, request.PageNumber, request.PageSize);
        }
    }
}
