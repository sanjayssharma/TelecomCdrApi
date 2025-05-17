using System.Text.Json.Serialization;
using TelecomCdr.Domain;

namespace TelecomCdr.Abstraction.Models
{
    public class FileProcessingResult
    {
        public bool Success => SuccessfulRecordsCount > 0 && FailedRecordsCount == 0;
        public int SuccessfulRecordsCount { get; set; }
        public int FailedRecordsCount { get; set; }
        [JsonIgnore]
        public List<string> ErrorMessages { get; } = new List<string>();
        public bool HasErrors => ErrorMessages.Count > 0;
        public bool HasCriticalErrors => FailedRecordsCount == -1;

        public ProcessingStatus DetermineStatus()
        {
            if (Success)
            {
                return ProcessingStatus.Succeeded;
            }
            else if (SuccessfulRecordsCount > 0 && FailedRecordsCount > 0)
            {
                return ProcessingStatus.PartiallySucceeded;
            }
            else if (FailedRecordsCount > 0 && SuccessfulRecordsCount == 0)
            {
                return ProcessingStatus.Failed;
            }
            else
            {
                return ProcessingStatus.Failed; // Default to failed if no records processed
            }
        }
    }
}
