using MediatR;
using TelecomCdr.Core.Models;
using TelecomCdr.Domain;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrsByCallerQuery : IRequest<PagedResponse<CallDetailRecord>>
    {
        public string CallerId { get; set; } = string.Empty;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }
}
