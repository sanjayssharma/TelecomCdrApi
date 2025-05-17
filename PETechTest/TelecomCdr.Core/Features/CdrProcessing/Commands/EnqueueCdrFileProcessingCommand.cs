using MediatR;
using Microsoft.AspNetCore.Http;

namespace TelecomCdr.Core.Features.CdrProcessing.Commands
{
    public class EnqueueCdrFileProcessingCommand : IRequest<Guid> // Returns JobId which is the CorrelationId
    {
        public IFormFile File { get; set; }
        public Guid CorrelationId { get; set; }

        public EnqueueCdrFileProcessingCommand(IFormFile file, Guid correlationId)
        {
            File = file ?? throw new ArgumentNullException(nameof(file));
            CorrelationId = correlationId != Guid.Empty ? correlationId : throw new ArgumentNullException(nameof(correlationId));
        }
    }
}
