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

        /// <summary>
        /// Private helper method to get a BlobClient and verify its existence.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <param name="operationName">Name of the operation calling this helper, for logging.</param>
        /// <returns>A configured BlobClient if the blob exists.</returns>
        /// <exception cref="FileNotFoundException">Thrown if the blob does not exist.</exception>
        private async Task<BlobClient> GetAndVerifyBlobClientAsync(string containerName, string blobName, string operationName)
        {
            BlobContainerClient containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            BlobClient blobClient = containerClient.GetBlobClient(blobName);

            if (!await blobClient.ExistsAsync())
            {
                _logger?.LogWarning($"Blob '{blobName}' in container '{containerName}' not found during {operationName}.");
                throw new FileNotFoundException($"Blob '{blobName}' not found in container '{containerName}'. Cannot perform {operationName}.");
            }
            return blobClient;
        }

        /// <summary>
        /// Gets the properties (size and metadata) of a blob.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <returns>A tuple containing the blob size and its metadata.</returns>
        public async Task<(long Size, IDictionary<string, string> Metadata)> GetBlobPropertiesAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            try
            {
                // Use the helper method to get and verify the blob client
                BlobClient blobClient = await GetAndVerifyBlobClientAsync(containerName, blobName, nameof(GetBlobPropertiesAsync));

                BlobProperties properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
                return (properties.ContentLength, properties.Metadata);
            }
            catch (FileNotFoundException) // Already logged by the helper
            {
                throw; // Re-throw FileNotFoundException to be handled by the caller
            }
            catch (Azure.RequestFailedException ex)
            {
                _logger?.LogError(ex, $"Azure SDK error getting properties for blob '{blobName}' in container '{containerName}': {ex.Message}");
                throw; // Re-throw to allow higher-level handling
            }
        }

        /// <summary>
        /// Uploads a stream to a blob, optionally with metadata.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="blobName">The name of the blob to create or overwrite.</param>
        /// <param name="content">The stream content to upload.</param>
        /// <param name="metadata">Optional metadata to associate with the blob.</param>
        public async Task<Uri> UploadFileAsync(string containerName, string blobName, Stream content, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        {
            if (content == null)
            { 
                throw new ArgumentNullException(nameof(content)); 
            }

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
                    //HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
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
            catch (Azure.RequestFailedException ex)
            {
                _logger?.LogError(ex, $"Azure SDK error uploading blob '{blobName}' to container '{containerName}': {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading blob {BlobName} to container {ContainerName}.", blobName, containerName);
                throw;
            }
        }

        /// <summary>
        /// Downloads a blob as a stream.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <returns>A stream containing the blob's content.</returns>
        /// <remarks>The caller is responsible for disposing the returned stream.</remarks>
        public async Task<Stream> DownloadFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(blobName)) throw new ArgumentException("Blob name cannot be null or empty.", nameof(blobName));

            _logger.LogDebug("Attempting to download blob '{BlobName}' from container '{ContainerName}'.", blobName, containerName);

            try
            {
                BlobClient blobClient = await GetAndVerifyBlobClientAsync(containerName, blobName, nameof(DownloadFileAsync));

                _logger.LogInformation("Downloading blob {BlobName} from container {ContainerName}.", blobName, containerName);
                BlobDownloadInfo download = await blobClient.DownloadAsync(cancellationToken);
                _logger.LogInformation("Successfully initiated download for blob {BlobName}.", blobName);
                return download.Content;
            }
            catch (FileNotFoundException)
            {
                throw; // Re-throw FileNotFoundException to be handled by the caller
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading blob {BlobName} from container {ContainerName}.", blobName, containerName);
                throw;
            }
        }

        /// <summary>
        /// Deletes a blob if it exists.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="blobName">The name of the blob to delete.</param>
        public async Task DeleteFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            try
            {
                BlobClient blobClient = await GetAndVerifyBlobClientAsync(containerName, blobName, nameof(DownloadFileAsync));

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
