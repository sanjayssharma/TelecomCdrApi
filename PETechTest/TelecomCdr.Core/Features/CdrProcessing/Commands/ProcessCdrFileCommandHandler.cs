using MediatR;
using Microsoft.Extensions.Logging;
using TelecomCdr.Core.Interfaces;

namespace TelecomCdr.Core.Features.CdrProcessing.Commands
{
    public class ProcessCdrFileCommandHandler : IRequestHandler<ProcessCdrFileCommand, Unit>
    {
        private readonly IFileProcessingService _fileProcessingService;
        private readonly ILogger<ProcessCdrFileCommandHandler> _logger;

        public ProcessCdrFileCommandHandler(
            IFileProcessingService fileProcessingService,
            ILogger<ProcessCdrFileCommandHandler> logger)
        {
            _fileProcessingService = fileProcessingService ?? throw new ArgumentNullException(nameof(fileProcessingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Unit> Handle(ProcessCdrFileCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing CDR file {FileName} with CorrelationId {CorrelationId} for direct storage.", request.File.FileName, request.CorrelationId);

            using var stream = request.File.OpenReadStream();
            // Pass the request's CorrelationId as the uploadCorrelationId for the records
            await _fileProcessingService.ProcessAndStoreCdrFileAsync(stream, request.CorrelationId, cancellationToken);

            _logger.LogInformation("Successfully processed CDR file {FileName} with CorrelationId {CorrelationId} for direct storage.", request.File.FileName, request.CorrelationId);
            return Unit.Value;
        }
    }
}
