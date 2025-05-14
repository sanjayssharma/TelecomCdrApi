using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Service;

namespace TelecomCdr.Infrastructure.Services
{
    public class AzureBlobStorageService : IBlobStorageService
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<AzureBlobStorageService> _logger;

        public AzureBlobStorageService(BlobServiceClient blobServiceClient, ILogger<AzureBlobStorageService> logger)
        {
            _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Uri> UploadFileAsync(string containerName, string blobName, Stream content, string contentType, IDictionary<string, string>? metadata, CancellationToken cancellationToken = default)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (string.IsNullOrWhiteSpace(blobName)) throw new ArgumentException("Blob name cannot be null or empty.", nameof(blobName));

            _logger.LogInformation("Attempting to upload blob '{BlobName}' to container '{ContainerName}'...", blobName, containerName);

            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

                var blobClient = containerClient.GetBlobClient(blobName);

                // Set upload options, including headers and metadata
                var uploadOptions = new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                    Metadata = metadata // Pass the metadata dictionary here
                };

                // Ensure the stream is at the beginning before uploading
                if (content.CanSeek)
                {
                    content.Seek(0, SeekOrigin.Begin);
                }

                _logger.LogInformation("Uploading blob {BlobName} to container {ContainerName}.", blobName, containerName);
                await blobClient.UploadAsync(content, uploadOptions, cancellationToken: cancellationToken);
                _logger.LogInformation("Successfully uploaded blob {BlobName} to {Uri}", blobName, blobClient.Uri);
                return blobClient.Uri;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading blob {BlobName} to container {ContainerName}.", blobName, containerName);
                throw;
            }
        }

        public async Task<Stream> DownloadFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(blobName)) throw new ArgumentException("Blob name cannot be null or empty.", nameof(blobName));

            _logger.LogDebug("Attempting to download blob '{BlobName}' from container '{ContainerName}'.", blobName, containerName);

            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                if (!await blobClient.ExistsAsync(cancellationToken))
                {
                    _logger.LogError("Blob {BlobName} not found in container {ContainerName}.", blobName, containerName);
                    throw new FileNotFoundException($"Blob {blobName} not found in container {containerName}.");
                }

                _logger.LogInformation("Downloading blob {BlobName} from container {ContainerName}.", blobName, containerName);
                BlobDownloadInfo download = await blobClient.DownloadAsync(cancellationToken);
                _logger.LogInformation("Successfully initiated download for blob {BlobName}.", blobName);
                return download.Content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading blob {BlobName} from container {ContainerName}.", blobName, containerName);
                throw;
            }
        }

        public async Task DeleteFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                _logger.LogInformation("Deleting blob {BlobName} from container {ContainerName}.", blobName, containerName);
                await blobClient.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
                _logger.LogInformation("Successfully deleted blob {BlobName} (if it existed).", blobName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting blob {BlobName} from container {ContainerName}.", blobName, containerName);
                throw;
            }
        }
    }
}
