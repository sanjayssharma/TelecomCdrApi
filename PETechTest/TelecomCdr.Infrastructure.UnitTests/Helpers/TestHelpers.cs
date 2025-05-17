using System.Text;
using TelecomCdr.Domain;

namespace TelecomCdr.Infrastructure.UnitTests.Helpers
{
    public class TestHelpers
    {
        public static MemoryStream CreateCsvStreamFromString(string csvContent)
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(csvContent));
        }

        public static CdrFileRecordDto GenerateValidTestCdrRecordDto(int index)
        {
            return new CdrFileRecordDto
            {
                CallerId = $"Caller{index}",
                Recipient = $"Recipient{index}",
                CallDate = DateTime.Now.AddDays(-index).ToString("dd/MM/yyyy"),
                EndTime = DateTime.Now.AddHours(-index).ToString("HH\\:mm\\:ss"),
                Duration = 60 + index,
                Cost = 1.23m + (index * 0.1m),
                Reference = $"Ref{index:D5}", // Ensure unique reference
                Currency = "GBP"
            };
        }

        public static string GenerateCsvContent(IEnumerable<CdrFileRecordDto> records, bool includeHeader = true)
        {
            var sb = new StringBuilder();
            if (includeHeader)
            {
                sb.AppendLine("caller_id,recipient,call_date,end_time,duration,cost,reference,currency");
            }
            foreach (var record in records)
            {
                sb.AppendLine($"{record.CallerId},{record.Recipient},{record.CallDate},{record.EndTime},{record.Duration},{record.Cost},{record.Reference},{record.Currency}");
            }
            return sb.ToString();
        }
    }
}
