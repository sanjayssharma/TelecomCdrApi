using MediatR;
using Microsoft.Extensions.Logging;
using TelecomCdr.Core.Interfaces;
using TelecomCdr.Core.Models.DomainModels;
using TelecomCdr.Core.Models.DTO;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrsByRecipientQueryHandler : IRequestHandler<GetCdrsByRecipientQuery, PagedResponse<CallDetailRecord>>
    {
        private readonly ICdrRepository _cdrRepository;
        private readonly ILogger<GetCdrsByRecipientQueryHandler> _logger;

        public GetCdrsByRecipientQueryHandler(ICdrRepository cdrRepository, ILogger<GetCdrsByRecipientQueryHandler> logger)
        {
            _cdrRepository = cdrRepository;
            _logger = logger;
        }

        public async Task<PagedResponse<CallDetailRecord>> Handle(GetCdrsByRecipientQuery request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Fetching CDRs by Recipient: {Recipient}", request.RecipientId);

            var (items, totalCount) = await _cdrRepository.GetByRecipientIdAsync(
                request.RecipientId,
                request.PageNumber,
                request.PageSize);

            // Use the factory method that takes pre-fetched items and count
            return PagedResponse<CallDetailRecord>.Create(items, totalCount, request.PageNumber, request.PageSize);
        }
    }
}
