using MediatR;
using Microsoft.Extensions.Logging;
using TelecomCdr.Domain;
using TelecomCdr.Core.Models;
using TelecomCdr.Abstraction.Interfaces.Repository;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrsByCallerQueryHandler : IRequestHandler<GetCdrsByCallerQuery, PagedResponse<CallDetailRecord>>
    {
        private readonly ICdrRepository _cdrRepository;
        private readonly ILogger<GetCdrsByCallerQueryHandler> _logger;

        public GetCdrsByCallerQueryHandler(ICdrRepository cdrRepository, ILogger<GetCdrsByCallerQueryHandler> logger)
        {
            _cdrRepository = cdrRepository;
            _logger = logger;
        }

        public async Task<PagedResponse<CallDetailRecord>> Handle(GetCdrsByCallerQuery request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Fetching CDRs by Caller: {CallerId}", request.CallerId);
            // Validate request parameters (e.g., PageNumber > 0, PageSize > 0) if not handled by model binding/attributes

            var (items, totalCount) = await _cdrRepository.GetByCallerIdAsync(
                request.CallerId,
                request.PageNumber,
                request.PageSize);

            // Use the factory method that takes pre-fetched items and count
            return PagedResponse<CallDetailRecord>.Create(items, totalCount, request.PageNumber, request.PageSize);
        }
    }
}
