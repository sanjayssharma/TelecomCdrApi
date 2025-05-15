namespace TelecomCdr.Abstraction.Models
{
    public class FileProcessingResult
    {
        public bool Success { get; set; }
        public int ProcessedRecordsCount { get; set; }
        public int FailedRecordsCount { get; set; }
        public List<string> ErrorMessages { get; } = new List<string>();
    }
}
