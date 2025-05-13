using MediatR;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using TelecomCdr.Core.Features.CdrProcessing.Commands;
using TelecomCdr.Core.Features.CdrRetrieval.Queries;
using TelecomCdr.Core.Filters;
using TelecomCdr.Core.Models.DomainModels;
using TelecomCdr.Core.Models.DTO;

namespace TelecomCdr.API.Controllers
{
    [ApiController]
    [Route("api/cdr")]
    public class CdrController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly ILogger<CdrController> _logger;

        public CdrController(IMediator mediator, ILogger<CdrController> logger)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost("upload")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        [ServiceFilter(typeof(IdempotencyAttribute))]
        public async Task<IActionResult> UploadCdrFile([Required] IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                _logger.LogWarning("UploadCdrFile called with no file or empty file.");
                return BadRequest(new { Message = "File is required and cannot be empty." });
            }
            if (file.ContentType != "text/csv" && file.ContentType != "application/vnd.ms-excel" && file.ContentType != "application/octet-stream")
            {
                _logger.LogWarning("UploadCdrFile called with invalid file type: {ContentType}", file.ContentType);
                return BadRequest(new { Message = "Invalid file type. Please upload a CSV file." });
            }

            if (!Guid.TryParse(HttpContext.Items["CorrelationId"]?.ToString(), out var correlationId))
            {
                correlationId = Guid.NewGuid();
            }

            if (correlationId == Guid.Empty)
            {
                correlationId = Guid.NewGuid();
            }

            _logger.LogInformation("Received request to upload CDR file: {FileName}, Size: {FileSize}, CorrelationId: {CorrelationId}",
                file.FileName, file.Length, correlationId);

            var command = new ProcessCdrFileCommand(file, correlationId);
            await _mediator.Send(command);

            return Accepted(new { Message = "File received and processing started.", CorrelationId = correlationId });
        }

        [HttpPost("upload-large")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        [ServiceFilter(typeof(IdempotencyAttribute))]
        public async Task<IActionResult> UploadLargeCdrFile([Required] IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                _logger.LogWarning("UploadLargeCdrFile called with no file or empty file.");
                return BadRequest(new { Message = "File is required and cannot be empty." });
            }
            if (file.ContentType != "text/csv" && file.ContentType != "application/vnd.ms-excel" && file.ContentType != "application/octet-stream")
            {
                _logger.LogWarning("UploadLargeCdrFile called with invalid file type: {ContentType}", file.ContentType);
                return BadRequest(new { Message = "Invalid file type. Please upload a CSV file." });
            }
            
            if (!Guid.TryParse(HttpContext.Items["CorrelationId"]?.ToString(), out var correlationId))
            { 
                correlationId = Guid.NewGuid();
            }

            if (correlationId == Guid.Empty)
            {
                correlationId = Guid.NewGuid();
            }

            _logger.LogInformation("Received request to upload large CDR file for background processing: {FileName}, Size: {FileSize}, CorrelationId: {CorrelationId}",
               file.FileName, file.Length, correlationId);

            try
            {
                var command = new EnqueueCdrFileProcessingCommand(file, correlationId);
                var jobId = await _mediator.Send(command);

                return Accepted(new { Message = "File received and queued for background processing via blob storage.", JobId = jobId, CorrelationId = correlationId });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Blob storage is not configured"))
            {
                _logger.LogError(ex, "Cannot process large file upload. Azure Blob Storage is not configured. CorrelationId: {CorrelationId}", correlationId);
                return StatusCode(StatusCodes.Status501NotImplemented, new { Message = "Large file upload is not available because blob storage is not configured.", CorrelationId = correlationId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing file {FileName} for background processing. CorrelationId: {CorrelationId}", file.FileName, correlationId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "An error occurred while preparing the file for background processing.", CorrelationId = correlationId });
            }
        }


        [HttpGet("reference/{reference}")]
        [ProducesResponseType(typeof(CallDetailRecord), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetCdrByReference(string reference)
        {
            _logger.LogInformation("Request to get CDR by reference: {Reference}", reference);
            var query = new GetCdrByReferenceQuery(reference);
            var result = await _mediator.Send(query);
            return result != null ? Ok(result) : NotFound(new { Message = $"CDR with reference '{reference}' not found." });
        }

        // GET /api/cdr/caller/{callerId}?pageNumber=1&pageSize=20
        [HttpGet("caller/{callerId}")]
        [ProducesResponseType(typeof(PagedResponse<CallDetailRecord>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetCdrsByCallerId(string callerId, [FromQuery] PaginationQuery paginationQuery)
        {
            if (string.IsNullOrWhiteSpace(callerId))
            {
                return BadRequest("Caller ID cannot be empty.");
            }

            _logger.LogInformation("Fetching CDRs for Caller ID: {CallerId}, PageNumber: {PageNumber}, PageSize: {PageSize}",
                callerId, paginationQuery.PageNumber, paginationQuery.PageSize);

            var query = new GetCdrsByCallerQuery
            {
                CallerId = callerId,
                PageNumber = paginationQuery.PageNumber,
                PageSize = paginationQuery.PageSize
            };

            var result = await _mediator.Send(query);
            return Ok(result);
        }

        // GET /api/cdr/recipient/{recipientId}?pageNumber=1&pageSize=20
        [HttpGet("recipient/{recipientId}")]
        [ProducesResponseType(typeof(PagedResponse<CallDetailRecord>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetCdrsByRecipientId(string recipientId, [FromQuery] PaginationQuery paginationQuery)
        {
            if (string.IsNullOrWhiteSpace(recipientId))
            {
                return BadRequest("Recipient ID cannot be empty.");
            }

            _logger.LogInformation("Fetching CDRs for Recipient ID: {RecipientId}, PageNumber: {PageNumber}, PageSize: {PageSize}",
                recipientId, paginationQuery.PageNumber, paginationQuery.PageSize);

            var query = new GetCdrsByRecipientQuery
            {
                RecipientId = recipientId,
                PageNumber = paginationQuery.PageNumber,
                PageSize = paginationQuery.PageSize
            };

            var result = await _mediator.Send(query);
            return Ok(result);
        }

        // GET /api/cdr/correlation/{correlationId}?pageNumber=1&pageSize=20
        [HttpGet("correlation/{correlationId}")]
        [ProducesResponseType(typeof(PagedResponse<CallDetailRecord>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetCdrsByCorrelationId(Guid correlationId, [FromQuery] PaginationQuery paginationQuery)
        {
            if (correlationId != Guid.Empty)
            {
                return BadRequest("Correlation ID cannot be empty or default GUID.");
            }

            _logger.LogInformation("Fetching CDRs for Correlation ID: {CorrelationId}, PageNumber: {PageNumber}, PageSize: {PageSize}",
                correlationId, paginationQuery.PageNumber, paginationQuery.PageSize);

            var query = new GetCdrsByUploadCorrelationIdQuery
            {
                CorrelationId = correlationId,
                PageNumber = paginationQuery.PageNumber,
                PageSize = paginationQuery.PageSize
            };

            var result = await _mediator.Send(query);
            return Ok(result);
        }
    }
}
