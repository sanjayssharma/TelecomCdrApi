namespace TelecomCdr.Abstraction.Models
{
    public class FileProcessingResult
    {
        public int SuccessfulRecords { get; set; }
        public int FailedRecords { get; set; }
        public List<string> ErrorMessages { get; } = new List<string>();
    }
}
