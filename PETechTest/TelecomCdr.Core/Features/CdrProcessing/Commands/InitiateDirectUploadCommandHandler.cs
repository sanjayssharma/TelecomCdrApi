using Azure.Storage.Sas;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Core.Configurations;
using TelecomCdr.Core.Models;

namespace TelecomCdr.Core.Features.CdrProcessing.Commands
{
    public class InitiateDirectUploadCommandHandler : IRequestHandler<InitiateDirectUploadCommand, InitiateUploadResponseDto>
    {
        private readonly IBlobStorageService _blobStorageService;
        private readonly FileUploadSettings _fileUploadSettings;
        private readonly ILogger<InitiateDirectUploadCommandHandler> _logger;
        private readonly IMediator _mediator; // dispatch further commands, e.g., for pre-creating job status

        private const int DefaultSasValidityMinutes = 30; // Default validity period for SAS URI if not configured

        public InitiateDirectUploadCommandHandler(
            IBlobStorageService blobStorageService,
            IOptions<FileUploadSettings> fileUploadSettingsOptions, // Injected as IOptions
            ILogger<InitiateDirectUploadCommandHandler> logger,
            IMediator mediator)
        {
            _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
            _fileUploadSettings = fileUploadSettingsOptions?.Value ?? throw new ArgumentNullException(nameof(fileUploadSettingsOptions), "FileUploadSettings cannot be null. Ensure it's configured.");
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediator = mediator;
        }

        public async Task<InitiateUploadResponseDto> Handle(InitiateDirectUploadCommand command, CancellationToken cancellationToken)
        {
            var request = command.UploadRequest;

            _logger.LogInformation("Handling InitiateDirectUploadCommand for file: {FileName}, ContentType: {ContentType}", request.FileName, request.ContentType);

            // Generate a unique Job ID. This ID will correlate the uploaded file with its processing lifecycle.
            string jobId = Guid.NewGuid().ToString();

            // Sanitize the original filename and create a unique blob name.
            // Storing in a "folder" per job ID helps organize blobs and avoid name collisions.
            string originalFileName = Path.GetFileName(request.FileName); // Ensures only the file name part is used
            string uniqueBlobName = $"{jobId}/{originalFileName}";

            // Get the target container name from configured settings.
            string containerName = _fileUploadSettings.UploadContainerName;
            if (string.IsNullOrWhiteSpace(containerName))
            {
                _logger.LogError("UploadContainerName is not configured in FileUploadSettings.");
                throw new InvalidOperationException("Upload container name is not configured.");
            }

            // Define the validity period for the SAS URI from configured settings.
            var validityPeriod = TimeSpan.FromMinutes(_fileUploadSettings.SasValidityMinutes);
            if (_fileUploadSettings.SasValidityMinutes <= 0)
            {
                _logger.LogWarning("SasValidityMinutes is configured to {Minutes}, which is invalid. Defaulting to 30 minutes.", _fileUploadSettings.SasValidityMinutes);
                validityPeriod = TimeSpan.FromMinutes(DefaultSasValidityMinutes); // Fallback to a sensible default if configuration is invalid
            }

            // Define the permissions for the SAS URI. Write and Create are needed for uploading a new blob.
            var permissions = BlobSasPermissions.Write | BlobSasPermissions.Create;

            // Call the blob storage service to generate the SAS URI.
            Uri sasUri = await _blobStorageService.GenerateBlobUploadSasUriAsync(
                containerName,
                uniqueBlobName,
                validityPeriod,
                permissions,
                request.ContentType); // Pass content type, which might be used by the SAS generation or client

            // Prepare the response DTO for the client.
            var response = new InitiateUploadResponseDto
            {
                UploadUrl = sasUri.ToString(),
                BlobName = uniqueBlobName,
                ContainerName = containerName,
                JobId = jobId,
                ExpiresOn = DateTimeOffset.UtcNow.Add(validityPeriod)
            };

            _logger.LogInformation("Successfully generated SAS URI for direct upload via command handler. JobId: {JobId}, BlobName: {BlobName}, Container: {ContainerName}. SAS expires at {ExpiryTime}",
                jobId, uniqueBlobName, containerName, response.ExpiresOn);

            // Optional: At this point, we could create an initial record for the job in our database
            // with a status like "PendingUpload" or "UploadInitiated".
            // But since this is a direct upload, we might want to wait until the upload is completed.
            // await _mediator.Send(new CreateInitialJobStatusCommand { JobId = jobId, FileName = originalFileName, Status = "PendingUpload" }, cancellationToken);

            return response;
        }
    }
}
