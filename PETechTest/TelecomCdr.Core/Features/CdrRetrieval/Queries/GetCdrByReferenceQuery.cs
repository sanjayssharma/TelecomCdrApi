using MediatR;
using TelecomCdr.Domain;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrByReferenceQuery : IRequest<CallDetailRecord?>
    {
        public string Reference { get; }
        public GetCdrByReferenceQuery(string reference) => Reference = reference;
    }
}
