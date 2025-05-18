namespace TelecomCdr.Core.Models
{
    /// <summary>
    /// DTO for the response after initiating a direct upload.
    /// </summary>
    public class InitiateUploadResponseDto
    {
        /// <summary>
        /// The SAS URI that the client will use to upload the file directly to Azure Blob Storage.
        /// </summary>
        public string UploadUrl { get; set; }

        /// <summary>
        /// The name of the blob where the file will be stored. 
        /// This might be a sanitized or unique version of the original filename.
        /// </summary>
        public string BlobName { get; set; }

        /// <summary>
        /// The container where the blob will be stored.
        /// </summary>
        public string ContainerName { get; set; }

        /// <summary>
        /// A unique Job ID associated with this upload, which can be used to track processing status later.
        /// </summary>
        public string JobId { get; set; }

        /// <summary>
        /// The timestamp when the SAS URI expires.
        /// </summary>
        public System.DateTimeOffset ExpiresOn { get; set; }
    }
}
