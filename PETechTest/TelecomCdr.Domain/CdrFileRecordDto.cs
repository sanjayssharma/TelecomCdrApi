namespace TelecomCdr.Domain
{
    // Temporary DTO for CsvHelper mapping - still reads original CSV columns
    public class CdrFileRecordDto
    {
        public string CallerId { get; set; }
        public string Recipient { get; set; }
        public string CallDate { get; set; } // Read as string from CSV "call_date"
        public string EndTime { get; set; }  // Read as string from CSV "end_time"
        public int? Duration { get; set; }
        public decimal? Cost { get; set; }
        public string Reference { get; set; }
        public string Currency { get; set; }
    }
}
