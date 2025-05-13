using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using TelecomCdr.Core.Infrastructure.Persistence;
using TelecomCdr.Core.Interfaces;
using TelecomCdr.Core.Models.DomainModels;

namespace TelecomCdr.Core.Infrastructure.Services
{
    public class CsvFileProcessingService : IFileProcessingService
    {
        private readonly ICdrRepository _cdrRepository;
        private readonly AppDbContext _dbContext;
        private readonly IBlobStorageService _blobStorageService;
        private readonly ILogger<CsvFileProcessingService> _logger;
        private const int BatchSize = 1000;

        public CsvFileProcessingService(
            ICdrRepository cdrRepository,
            AppDbContext dbContext,
            IBlobStorageService blobStorageService,
            ILogger<CsvFileProcessingService> logger)
        {
            _cdrRepository = cdrRepository ?? throw new ArgumentNullException(nameof(cdrRepository));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ProcessAndStoreCdrFileAsync(Stream fileStream, Guid uploadCorrelationId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Processing and storing CDR data from stream. UploadCorrelationId: {UploadCorrelationId}", uploadCorrelationId);
            await ProcessInternalAsync(fileStream, uploadCorrelationId, cancellationToken);
        }

        public async Task ProcessAndStoreCdrFileFromBlobAsync(string containerName, string blobName, Guid originalUploadCorrelationId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Processing and storing CDR data from blob: {ContainerName}/{BlobName}. OriginalUploadCorrelationId: {OriginalUploadCorrelationId}",
                containerName, blobName, originalUploadCorrelationId);

            Stream fileStream;
            try
            {
                fileStream = await _blobStorageService.DownloadFileAsync(containerName, blobName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download blob {BlobName} from container {ContainerName} for processing. OriginalUploadCorrelationId: {OriginalUploadCorrelationId}",
                    blobName, containerName, originalUploadCorrelationId);
                return;
            }

            using (fileStream)
            {
                await ProcessInternalAsync(fileStream, originalUploadCorrelationId, cancellationToken);
            }
        }

        private async Task ProcessInternalAsync(Stream fileStream, Guid uploadCorrelationId, CancellationToken cancellationToken)
        {
            var recordsBatch = new List<CallDetailRecord>();
            int totalProcessed = 0;
            int totalFailed = 0;
            var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true, MissingFieldFound = null, HeaderValidated = null, TrimOptions = TrimOptions.Trim };

            using var reader = new StreamReader(fileStream);
            using var csv = new CsvReader(reader, config);
            csv.Context.RegisterClassMap<CdrFileRecordDtoMap>(); // Ensure DTO mapping is used

            await foreach (var recordDto in csv.GetRecordsAsync<CdrFileRecordDto>(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Parse date and time components
                    var callDatePart = DateTime.ParseExact(recordDto.CallDate, "dd/MM/yyyy", CultureInfo.InvariantCulture);
                    var endTimePart = TimeSpan.ParseExact(recordDto.EndTime, "HH:mm:ss", CultureInfo.InvariantCulture);
                    //var callEndDateTime = callDatePart.Date + endTimePart;

                    var cdr = new CallDetailRecord(
                        recordDto.CallerId,
                        recordDto.Recipient,
                        callDatePart,
                        endTimePart,
                        recordDto.Duration,
                        recordDto.Cost,
                        recordDto.Reference,
                        recordDto.Currency,
                        uploadCorrelationId
                    );
                    recordsBatch.Add(cdr);
                    totalProcessed++;

                    if (recordsBatch.Count >= BatchSize)
                    {
                        await _cdrRepository.AddBatchAsync(recordsBatch, cancellationToken);
                        await _dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogInformation("Saved batch of {BatchCount} CDRs. Total processed so far: {TotalProcessed}. UploadCorrelationId: {UploadCorrelationId}", recordsBatch.Count, totalProcessed, uploadCorrelationId);
                        recordsBatch.Clear();
                    }
                }
                catch (FormatException ex)
                {
                    totalFailed++;
                    _logger.LogWarning(ex, "Failed to parse date/time for record with reference {Reference}. CSV Date: '{CsvDate}', CSV Time: '{CsvTime}'. UploadCorrelationId: {UploadCorrelationId}. Skipping.", recordDto.Reference, recordDto.CallDate, recordDto.EndTime, uploadCorrelationId);
                }
                catch (Exception ex)
                {
                    totalFailed++;
                    _logger.LogWarning(ex, "Failed to process or map record for reference {Reference}. UploadCorrelationId: {UploadCorrelationId}. Skipping.", recordDto.Reference, uploadCorrelationId);
                }
            }

            // TODO: What happens to the failed records? Because they are not saved and lost. Hence, this would be a partial upload.

            if (recordsBatch.Any())
            {
                await _cdrRepository.AddBatchAsync(recordsBatch, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Saved final batch of {BatchCount} CDRs. Total processed: {TotalProcessed}. UploadCorrelationId: {UploadCorrelationId}", recordsBatch.Count, totalProcessed, uploadCorrelationId);
            }

            _logger.LogInformation("Finished processing CDR data. Total processed: {TotalProcessed}, Total failed: {TotalFailed}. UploadCorrelationId: {UploadCorrelationId}", totalProcessed, totalFailed, uploadCorrelationId);
        }

        // DTO for CsvHelper mapping - still reads original CSV columns
        private class CdrFileRecordDto
        {
            public string CallerId { get; set; }
            public string Recipient { get; set; }
            public string CallDate { get; set; } // Read as string from CSV "call_date"
            public string EndTime { get; set; }  // Read as string from CSV "end_time"
            public int Duration { get; set; }
            public decimal Cost { get; set; }
            public string Reference { get; set; }
            public string Currency { get; set; }
        }

        // CsvHelper ClassMap to map CSV headers to DTO properties
        private sealed class CdrFileRecordDtoMap : ClassMap<CdrFileRecordDto>
        {
            public CdrFileRecordDtoMap()
            {
                Map(m => m.CallerId).Name("caller_id");
                Map(m => m.Recipient).Name("recipient");
                Map(m => m.CallDate).Name("call_date"); // Maps "call_date" CSV header to CallDate DTO property
                Map(m => m.EndTime).Name("end_time");   // Maps "end_time" CSV header to EndTime DTO property
                Map(m => m.Duration).Name("duration");
                Map(m => m.Cost).Name("cost");
                Map(m => m.Reference).Name("reference");
                Map(m => m.Currency).Name("currency");
            }
        }
    }
}
