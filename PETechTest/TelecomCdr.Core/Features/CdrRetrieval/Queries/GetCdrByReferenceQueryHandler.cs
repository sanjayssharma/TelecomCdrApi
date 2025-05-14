using MediatR;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Domain;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrByReferenceQueryHandler : IRequestHandler<GetCdrByReferenceQuery, CallDetailRecord?>
    {
        private readonly ICdrRepository _cdrRepository;
        private readonly ILogger<GetCdrByReferenceQueryHandler> _logger;

        public GetCdrByReferenceQueryHandler(ICdrRepository cdrRepository, ILogger<GetCdrByReferenceQueryHandler> logger)
        {
            _cdrRepository = cdrRepository;
            _logger = logger;
        }

        public async Task<CallDetailRecord?> Handle(GetCdrByReferenceQuery request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Fetching CDR by reference: {Reference}", request.Reference);
            return await _cdrRepository.GetByReferenceAsync(request.Reference, cancellationToken);
        }
    }
}
