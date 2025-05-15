namespace TelecomCdr.Abstraction.Interfaces.Service
{
    public interface IBlobStorageService
    {
        Task<(long Size, IDictionary<string, string> Metadata)> GetBlobPropertiesAsync(string containerName, string blobName, CancellationToken cancellationToken = default);
        Task<Stream> DownloadFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default);
        Task<Uri> UploadFileAsync(string containerName, string blobName, Stream content, Dictionary<string, string> metadata = null, CancellationToken cancellationToken = default);
        Task DeleteFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default); // Optional for cleanup
    }
}
