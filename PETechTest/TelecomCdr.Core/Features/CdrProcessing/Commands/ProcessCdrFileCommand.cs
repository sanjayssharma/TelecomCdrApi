using MediatR;
using Microsoft.AspNetCore.Http;

namespace TelecomCdr.Core.Features.CdrProcessing.Commands
{
    public class ProcessCdrFileCommand : IRequest<Unit>
    {
        public IFormFile File { get; set; }
        public Guid CorrelationId { get; set; } // This is the request's correlation ID

        public ProcessCdrFileCommand(IFormFile file, Guid correlationId)
        {
            File = file ?? throw new ArgumentNullException(nameof(file));
            CorrelationId = correlationId != Guid.Empty ? correlationId : throw new ArgumentNullException(nameof(correlationId));
        }
    }
}
