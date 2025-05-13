using MediatR;
using TelecomCdr.Core.Models.DomainModels;
using TelecomCdr.Core.Models.DTO;

namespace TelecomCdr.Core.Features.CdrRetrieval.Queries
{
    public class GetCdrsByRecipientQuery : IRequest<PagedResponse<CallDetailRecord>>
    {
        public string RecipientId { get; set; } = string.Empty;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }
}
