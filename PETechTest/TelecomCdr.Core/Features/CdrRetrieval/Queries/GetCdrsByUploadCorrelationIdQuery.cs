using MediatR;
using TelecomCdr.Core.Models.DomainModels;
using TelecomCdr.Core.Models.DTO;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrsByUploadCorrelationIdQuery : IRequest<PagedResponse<CallDetailRecord>>
    {
        public Guid CorrelationId { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }
}
