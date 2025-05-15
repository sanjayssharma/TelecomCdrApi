using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Service;

namespace TelecomCdr.Infrastructure.Services
{
    public class NullBlobStorageService : IBlobStorageService
    {
        private readonly ILogger<NullBlobStorageService> _logger;
        public NullBlobStorageService(ILogger<NullBlobStorageService> logger) => _logger = logger;

        public Task DeleteFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Blob storage not configured. DeleteFileAsync for {ContainerName}/{BlobName} was not performed.", containerName, blobName);
            return Task.CompletedTask;
        }
        public Task<Stream> DownloadFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            _logger.LogError("Blob storage not configured. DownloadFileAsync for {ContainerName}/{BlobName} cannot be performed.", containerName, blobName);
            throw new InvalidOperationException("Blob storage is not configured. Cannot download file.");
        }

        public Task<(long Size, IDictionary<string, string> Metadata)> GetBlobPropertiesAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            _logger.LogError("Blob storage not configured. GetBlobPropertiesAsync for {ContainerName}/{BlobName} cannot be performed.", containerName, blobName);
            throw new NotImplementedException();
        }

        public Task<Uri> UploadFileAsync(string containerName, string blobName, Stream content, Dictionary<string, string> metadata = null, CancellationToken cancellationToken = default)
        {
            _logger.LogError("Blob storage not configured. UploadFileAsync for {ContainerName}/{BlobName} cannot be performed.", containerName, blobName);
            throw new InvalidOperationException("Blob storage is not configured. Cannot upload file.");
        }
    }
}
