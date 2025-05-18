using Azure.Storage.Sas;

namespace TelecomCdr.Abstraction.Interfaces.Service
{
    public interface IBlobStorageService
    {
        /// <summary>
        /// Retrieves the size and additional metadata of a blob.
        /// </summary>
        /// <param name="containerName"></param>
        /// <param name="blobName"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<(long Size, IDictionary<string, string> Metadata)> GetBlobPropertiesAsync(string containerName, string blobName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads a file from the specified container in blob storage.
        /// </summary>
        /// <param name="containerName"></param>
        /// <param name="blobName"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<Stream> DownloadFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads a file to the specified container in blob storage.
        /// </summary>
        /// <param name="containerName"></param>
        /// <param name="blobName"></param>
        /// <param name="content"></param>
        /// <param name="metadata"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<Uri> UploadFileAsync(string containerName, string blobName, Stream content, Dictionary<string, string> metadata = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a blob from the specified container.
        /// </summary>
        /// <param name="containerName"></param>
        /// <param name="blobName"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task DeleteFileAsync(string containerName, string blobName, CancellationToken cancellationToken = default); // Optional for cleanup

        /// <summary>
        /// Retrieves metadata for a blob.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <returns>A dictionary containing blob metadata, or null if blob not found.</returns>
        Task<IDictionary<string, string>> GetMetadataAsync(string containerName, string blobName);

        /// <summary>
        /// Sets or updates metadata for a blob.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <param name="metadata">A dictionary containing the metadata to set. This will replace existing metadata.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task SetMetadataAsync(string containerName, string blobName, IDictionary<string, string> metadata);

        // Direct to blob upload SAS URI generation
        /// <summary>
        /// Generates a SAS URI for uploading a blob directly to Azure Blob Storage.
        /// </summary>
        /// <param name="containerName"></param>
        /// <param name="blobName"></param>
        /// <param name="validityPeriod"></param>
        /// <param name="permissions"></param>
        /// <param name="contentType"></param>
        /// <returns></returns>
        public Task<Uri> GenerateBlobUploadSasUriAsync(string containerName, string blobName, TimeSpan validityPeriod, BlobSasPermissions permissions, string contentType = null);
    }
}
