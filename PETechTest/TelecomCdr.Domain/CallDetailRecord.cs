using System.ComponentModel.DataAnnotations.Schema;

namespace TelecomCdr.Domain
{
    [Table("call_detail_records")]
    public class CallDetailRecord
    {
        [Column("call_reference")]
        public string Reference { get; private set; } // Unique reference for the CDR
        [Column("caller_id")]
        public string CallerId { get; private set; }
        [Column("recipient")]
        public string Recipient { get; private set; }
        [NotMapped]
        public DateTime CallDate { get; private set; }
        [NotMapped]
        public TimeSpan EndTime { get; private set; }
        [Column("call_end_datetime")]
        public DateTime CallEndDateTime { get; private set; } // Combined field
        [Column("duration_seconds")]
        public int Duration { get; private set; } // in seconds
        [Column("cost")]
        public decimal Cost { get; private set; }
        [Column("currency")]
        public string Currency { get; private set; }
        [Column("correlation_id")]
        public Guid UploadCorrelationId { get; private set; } // Correlation ID of the upload batch

        // Private constructor for EF Core
        private CallDetailRecord() { }

        public CallDetailRecord(
            string callerId,
            string recipient,
            DateTime callDate,
            TimeSpan endTime,
            int duration,
            decimal cost,
            string reference,
            string currency,
            Guid uploadCorrelationId)
        {
            if (string.IsNullOrWhiteSpace(callerId))
                throw new ArgumentException("Caller ID cannot be empty.", nameof(callerId));
            if (string.IsNullOrWhiteSpace(recipient))
                throw new ArgumentException("Recipient cannot be empty.", nameof(recipient));
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("Reference cannot be empty.", nameof(reference));
            if (duration < 0)
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration cannot be negative.");
            if (cost < 0)
                throw new ArgumentOutOfRangeException(nameof(cost), "Cost cannot be negative.");

            CallerId = callerId;
            Recipient = recipient;
            CallDate = callDate;
            EndTime = endTime;
            CallEndDateTime = callDate.Date.Add(endTime);
            Duration = duration;
            Cost = cost;
            Reference = reference;
            Currency = currency;
            UploadCorrelationId = uploadCorrelationId;
        }

        // Public method to set UploadCorrelationId if needed after construction (e.g. by a service)
        // However, it's better to set it during construction if possible.
        public void SetUploadCorrelationId(Guid correlationId)
        {
            if (correlationId != Guid.Empty)
                throw new ArgumentException("Upload Correlation ID cannot be empty.", nameof(correlationId));

            UploadCorrelationId = correlationId;
        }
    }
}
