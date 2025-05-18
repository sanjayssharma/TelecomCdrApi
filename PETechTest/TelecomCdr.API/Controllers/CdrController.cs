using Azure.Storage.Sas;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Net;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Core.Features.CdrProcessing.Commands;
using TelecomCdr.Core.Features.CdrRetrieval.Queries;
using TelecomCdr.Core.Filters;
using TelecomCdr.Core.Models;
using TelecomCdr.Domain;

namespace TelecomCdr.API.Controllers
{
    [ApiController]
    [Route("api/cdr")]
    public class CdrController : ControllerBase
    {
        private const string DefaultUploadContainerKey = "FileUploadSettings:UploadContainerName";
        private const string DefaultUploadContainerName = "cdr-uploads"; // Fallback if not in config
        private const int DefaultSasValidityMinutes = 30;

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

            // Return relevant message
            return Ok(new { Message = "File received and processing started.", CorrelationId = correlationId });
        }

        [HttpPost("enqueue")]
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

        /// <summary>
        /// Initiates a direct-to-storage upload for a large CDR file.
        /// Generates a SAS URI that the client can use to upload the file directly to Azure Blob Storage.
        /// Logic is now delegated to InitiateDirectUploadCommandHandler.
        /// </summary>
        /// <param name="request">Details of the file to be uploaded (FileName, ContentType).</param>
        /// <returns>
        /// An <see cref="OkObjectResult"/> with the SAS URI, blob name, and Job ID.
        /// A <see cref="BadRequestObjectResult"/> if the request is invalid.
        /// </returns>
        /// <response code="200">Returns the SAS URI and details for direct upload.</response>
        /// <response code="400">If the request is invalid (e.g., missing filename, invalid content type).</response>
        /// <response code="500">If an unexpected error occurs while processing the request.</response>
        [HttpPost("initiate-direct-upload")]
        [ProducesResponseType(typeof(InitiateUploadResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> InitiateDirectUpload([FromBody] InitiateUploadRequestDto request)
        {
            // Basic model validation provided by ASP.NET Core
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("InitiateDirectUpload called with invalid model state.");
                return BadRequest(new ErrorResponseDto(HttpStatusCode.BadRequest)
                {
                    Message = "Invalid request parameters.",
                    Errors = ModelState.Values.SelectMany(v => v.Errors.Select(e => e.ErrorMessage)) 
                });
            }

            // Controller-level validation (can also be moved to a FluentValidation rule for the command)
            if (request.ContentType?.ToLowerInvariant() != "text/csv" &&
                Path.GetExtension(request.FileName)?.ToLowerInvariant() != ".csv")
            {
                _logger.LogWarning("InitiateDirectUpload called for a non-CSV file: {FileName}, ContentType: {ContentType}", request.FileName, request.ContentType);
                return BadRequest(new ErrorResponseDto(HttpStatusCode.BadRequest)
                {
                    Message = "Invalid file type. Only .csv files are supported for direct upload."
                });
            }

            try
            {
                // Create and dispatch the command to MediatR
                var command = new InitiateDirectUploadCommand(request);
                InitiateUploadResponseDto result = await _mediator.Send(command);

                _logger.LogInformation("Successfully processed InitiateDirectUpload request for JobId: {JobId}", result.JobId);
                return Ok(result);
            }
            catch (InvalidOperationException opEx) // Catch specific exceptions if needed
            {
                _logger.LogError(opEx, "Failed to process direct upload initiation for file {FileName} due to operational or configuration issue.", request.FileName);
                // Return a more generic error to the client for security/simplicity
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(HttpStatusCode.InternalServerError)
                {
                    Message = "An error occurred while preparing the file upload. Please try again later or contact support if the issue persists."
                });
            }
            catch (Exception ex)
            {
                // General exception handler
                _logger.LogError(ex, "Error processing initiate direct upload request for file {FileName}.", request.FileName);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(HttpStatusCode.InternalServerError)
                {
                    Message = "An unexpected error occurred while preparing the file upload."
                });
            }
        }
    }
}
