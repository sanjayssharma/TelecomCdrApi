namespace TelecomCdr.Core.Configurations
{
    public class FileUploadSettings
    {
        public const string SectionName = "FileUploadSettings";

        /// <summary>
        /// The name of the Azure Blob Storage container where files will be uploaded.
        /// </summary>
        public string UploadContainerName { get; set; } = "direct-cdr-uploads"; // Default value

        /// <summary>
        /// The validity period for the generated SAS URI in minutes.
        /// </summary>
        public int SasValidityMinutes { get; set; } = 30; // Default value
    }
}
