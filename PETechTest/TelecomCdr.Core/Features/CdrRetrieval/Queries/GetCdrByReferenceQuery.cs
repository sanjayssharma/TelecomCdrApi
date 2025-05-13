using MediatR;
using TelecomCdr.Core.Models.DomainModels;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrByReferenceQuery : IRequest<CallDetailRecord?>
    {
        public string Reference { get; }
        public GetCdrByReferenceQuery(string reference) => Reference = reference;
    }
}
