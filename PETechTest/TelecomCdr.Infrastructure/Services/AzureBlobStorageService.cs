using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
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
        /// Private helper method to get a BlobContainerClient.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="createIfNotExists">Whether to create the container if it does not exist.</param>
        /// <returns>A configured BlobContainerClient if the blob exists.</returns>
        /// <exception cref="FileNotFoundException">Thrown if the blob does not exist.</exception>
        private async Task<BlobContainerClient> GetContainerClient(string containerName, bool createIfNotExists = false)
        {
            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
                if (createIfNotExists)
                {
                    await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
                }
                return containerClient;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get or create container client for container: {ContainerName}", containerName);
                throw;
            }
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
                var containerClient = await GetContainerClient(containerName, true);
                var blobClient = containerClient.GetBlobClient(blobName);

                BlobProperties properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
                return (properties.ContentLength, properties.Metadata ?? new Dictionary<string, string>());
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
                var containerClient = await GetContainerClient(containerName, true);
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
                var containerClient = await GetContainerClient(containerName, true); // Ensure container exists
                var blobClient = containerClient.GetBlobClient(blobName);

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
                var containerClient = await GetContainerClient(containerName, true);
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

        /// <summary>
        /// Generates a Shared Access Signature (SAS) URI for uploading a blob.
        /// This implementation uses an Account SAS. For enhanced security with EntraId, consider User Delegation SAS.
        /// </summary>
        /// <param name="containerName">The name of the container where the blob will be uploaded.</param>
        /// <param name="blobName">The name of the blob to be created/updated.</param>
        /// <param name="validityPeriod">How long the SAS URI will be valid.</param>
        /// <param name="permissions">The permissions to grant (e.g., Write, Create).</param>
        /// <param name="contentType">Optional. The content type the client should set when uploading. 
        /// Note: While SAS can specify content type, it's often better for the client to set it and for the server to validate post-upload.</param>
        /// <returns>A SAS URI string that allows direct upload to the specified blob.</returns>
        public async Task<Uri> GenerateBlobUploadSasUriAsync(
            string containerName,
            string blobName,
            TimeSpan validityPeriod,
            BlobSasPermissions permissions,
            string contentType = null) // contentType parameter is present but not directly used to set SAS headers in this version
        {
            _logger.LogInformation("Generating SAS URI for blob {BlobName} in container {ContainerName} with permissions {Permissions} and validity {ValidityPeriod} seconds.",
                blobName, containerName, permissions, validityPeriod.TotalSeconds);

            try
            {
                // Get a reference to the container client.
                // This does not make a network call yet.
                var containerClient = await GetContainerClient(containerName, true);

                // Get a reference to the blob client.
                // This also does not make a network call yet.
                var blobClient = containerClient.GetBlobClient(blobName);

                // Check if the BlobClient can generate a SAS URI.
                if (!blobClient.CanGenerateSasUri)
                {
                    _logger.LogError("Cannot generate SAS URI. Check if BlobServiceClient was created with credentials (e.g., connection string with AccountKey or set up for User Delegation SAS).");
                    throw new InvalidOperationException("BlobClient is not configured to generate SAS URIs. Ensure it's created with appropriate credentials.");
                }

                // Create a BlobSasBuilder object to define SAS parameters.
                var sasBuilder = new BlobSasBuilder()
                {
                    BlobContainerName = containerName,
                    BlobName = blobName,
                    Resource = "b", // Specifies that the SAS is for a blob resource.
                    StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // Start time set to 5 minutes ago to avoid clock skew issues with clients.
                    ExpiresOn = DateTimeOffset.UtcNow.Add(validityPeriod) // Expiry time for the SAS.
                };

                // Set the permissions for the SAS.
                // For an upload, typically Write and Create are needed.
                // e.g., BlobSasPermissions.Write | BlobSasPermissions.Create
                sasBuilder.SetPermissions(permissions);

                // Optional: If you want to enforce specific headers via SAS (less common for upload, more for download)
                // if (!string.IsNullOrWhiteSpace(contentType))
                // {
                //     sasBuilder.ContentType = contentType;
                // }
                // It's generally better for the client to set the Content-Type header on the PUT request,
                // and for your backend to validate it after upload (e.g., from blob properties or Event Grid metadata).

                // Generate the SAS URI.
                // This example uses an Account SAS, which relies on the storage account key.
                // The BlobServiceClient must have been initialized with a connection string containing the account key,
                // or with a StorageSharedKeyCredential.
                Uri sasUri = blobClient.GenerateSasUri(sasBuilder);

                // For User Delegation SAS (more secure, uses EntraId, avoids exposing account key):
                // 1. The service principal/managed identity running this API needs "Storage Blob SAS Definer" or "Storage Blob Data Contributor" role.
                // 2. Get a user delegation key:
                //    UserDelegationKey userDelegationKey = await _blobServiceClient.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(validityPeriod));
                // 3. Generate SAS URI using the user delegation key:
                //    Uri sasUri = blobClient.GenerateSasUri(sasBuilder.ToSasQueryParameters(userDelegationKey, _blobServiceClient.AccountName));
                // This is preferred for production environments.

                _logger.LogInformation("Successfully generated SAS URI for blob {BlobName}: {SasUriPrefix}?<SAS_TOKEN_REDACTED>",
                                       blobName, sasUri.ToString().Split('?')[0]); // Log URI without the actual token for security.

                return sasUri;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating SAS URI for blob {BlobName} in container {ContainerName}", blobName, containerName);
                throw;
            }
        }

        /// <summary>
        /// Retrieves metadata for a blob.
        /// </summary>
        /// <param name="containerName"></param>
        /// <param name="blobName"></param>
        /// <returns></returns>
        public async Task<IDictionary<string, string>> GetMetadataAsync(string containerName, string blobName)
        {
            _logger.LogInformation("Getting metadata for blob {BlobName} from container {ContainerName}", blobName, containerName);
            try
            {
                var containerClient = await GetContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);
                BlobProperties properties = await blobClient.GetPropertiesAsync();
                _logger.LogInformation("Successfully retrieved metadata for blob {BlobName}", blobName);
                return properties.Metadata;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("Blob {BlobName} not found while getting metadata.", blobName);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting metadata for blob {BlobName}", blobName);
                throw;
            }
        }

        /// <summary>
        /// Sets or updates metadata for a blob.
        /// </summary>
        /// <param name="containerName"></param>
        /// <param name="blobName"></param>
        /// <param name="metadata"></param>
        /// <returns></returns>
        public async Task SetMetadataAsync(string containerName, string blobName, IDictionary<string, string> metadata)
        {
            _logger.LogInformation("Setting metadata for blob {BlobName} in container {ContainerName}. Metadata: {@Metadata}", blobName, containerName, metadata);
            try
            {
                var containerClient = await GetContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);
                await blobClient.SetMetadataAsync(metadata);
                _logger.LogInformation("Successfully set metadata for blob {BlobName}", blobName);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning(ex, "Blob {BlobName} not found in container {ContainerName} while trying to set metadata.", blobName, containerName);
                throw; // Or handle as appropriate (e.g., if blob disappearing is a possible race condition)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting metadata for blob {BlobName} in container {ContainerName}", blobName, containerName);
                throw;
            }
        }
    }
}
