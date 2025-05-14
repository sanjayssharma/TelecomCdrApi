using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;
using TelecomCdr.Infrastructure.Persistence.Repositories;

namespace TelecomCdr.Infrastructure.Services
{
    public class CsvFileProcessingService : IFileProcessingService
    {
        private readonly AppDbContext _dbContext;
        private readonly ICdrRepository _cdrRepository;
        private readonly IFailedCdrRecordRepository _failedRecordRepository;
        private readonly IBlobStorageService _blobStorageService;
        private readonly ILogger<CsvFileProcessingService> _logger;
        private const int BatchSize = 1000;

        public CsvFileProcessingService(
            ICdrRepository cdrRepository,
            IFailedCdrRecordRepository failedRecordRepository,
            AppDbContext dbContext,
            IBlobStorageService blobStorageService,
            ILogger<CsvFileProcessingService> logger)
        {
            _cdrRepository = cdrRepository ?? throw new ArgumentNullException(nameof(cdrRepository));
            _failedRecordRepository = failedRecordRepository ?? throw new ArgumentNullException(nameof(failedRecordRepository));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<FileProcessingResult> ProcessAndStoreCdrFileAsync(Stream fileStream, Guid uploadCorrelationId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Processing and storing CDR data from stream. UploadCorrelationId: {UploadCorrelationId}", uploadCorrelationId);

            var result = await ProcessInternalAsync(fileStream, uploadCorrelationId, cancellationToken);
            return result;
        }

        public async Task<FileProcessingResult> ProcessAndStoreCdrFileFromBlobAsync(string containerName, string blobName, Guid originalUploadCorrelationId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Processing and storing CDR data from blob: {ContainerName}/{BlobName}. OriginalUploadCorrelationId: {OriginalUploadCorrelationId}",
                containerName, blobName, originalUploadCorrelationId);

            var result = new FileProcessingResult();

            Stream fileStream;
            try
            {
                fileStream = await _blobStorageService.DownloadFileAsync(containerName, blobName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download blob {BlobName} from container {ContainerName} for processing. OriginalUploadCorrelationId: {OriginalUploadCorrelationId}",
                    blobName, containerName, originalUploadCorrelationId);

                result.ErrorMessages.Add($"Exception ocurred when trying to access the file: {blobName}, uploadCorrelationId: {originalUploadCorrelationId}");
                return result;
            }

            using (fileStream)
            {
                if (fileStream == null || fileStream == Stream.Null || fileStream.Length == 0)
                {
                    _logger.LogError("Blob {BlobName} from container {ContainerName} not found or is empty.", blobName, containerName);
                    
                    result.ErrorMessages.Add($"Blob {blobName} not found in container {containerName} or is empty.");
                    // No records processed or failed if file is missing/empty
                    return result; // Or throw an exception FileNotFoundException
                }
               return await ProcessInternalAsync(fileStream, originalUploadCorrelationId, cancellationToken);
            }
        }

        private async Task<FileProcessingResult> ProcessInternalAsync(Stream fileStream, Guid uploadCorrelationId, CancellationToken cancellationToken)
        {
            var result = new FileProcessingResult();
            //var recordsBatch = new List<CallDetailRecord>();
            var successfullCdrBatch = new List<CallDetailRecord>();
            var failedCdrBatch = new List<FailedCdrRecord>();
            
            int rowNumber = 0;

            var config = new CsvConfiguration(CultureInfo.InvariantCulture) { 
                HasHeaderRecord = true, 
                MissingFieldFound = null, 
                HeaderValidated = null, 
                TrimOptions = TrimOptions.Trim,
                BadDataFound = context => // Handle bad data per row
                {
                    _logger.LogWarning("Bad data found in CSV row: {RawRow}. Field: {Field}, Context: {Context}", context.RawRecord, context.Field, context.Context);
                }
            };

            try
            {
                using var reader = new StreamReader(fileStream);
                using var csv = new CsvReader(reader, config);
                csv.Context.RegisterClassMap<CdrFileRecordDtoMap>(); // Ensure DTO mapping is used

                await foreach (var recordDto in csv.GetRecordsAsync<CdrFileRecordDto>(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Get the raw string of the current row
                    var rawRow = csv.Context.Parser?.RawRecord ?? string.Empty;

                    // Granular Try-catch for each row.
                    try
                    {
                        rowNumber++;

                        // Validate the row and adjust the rules accordingly. So invalid data could be identified and recorded.
                        await ValidateRowAsync(recordDto);

                        // Parse date and time components
                        var callDatePart = DateTime.ParseExact(recordDto.CallDate, "dd/MM/yyyy", CultureInfo.InvariantCulture);
                        if (!TimeSpan.TryParse(recordDto.EndTime, out TimeSpan endTimePart))
                        {
                            _logger.LogWarning("Unable to parseEndTime for Reference {CdrReference}, CorrelationId: {CorrelationId}", recordDto.Reference, uploadCorrelationId);
                        }

                        var cdr = new CallDetailRecord(
                            recordDto.CallerId,
                            recordDto.Recipient,
                            callDatePart,
                            endTimePart,
                            recordDto.Duration.Value,
                            recordDto.Cost.Value,
                            recordDto.Reference,
                            recordDto.Currency,
                            uploadCorrelationId
                        );
                        successfullCdrBatch.Add(cdr);
                        result.SuccessfulRecords++;

                        if (successfullCdrBatch.Count >= BatchSize)
                        {
                            await _cdrRepository.AddBatchAsync(successfullCdrBatch, cancellationToken);
                            await _dbContext.SaveChangesAsync(cancellationToken);

                            _logger.LogInformation("Saved batch of {BatchCount} CDRs. Total processed so far: {TotalProcessed}. UploadCorrelationId: {UploadCorrelationId}", successfullCdrBatch.Count, result.SuccessfulRecords, uploadCorrelationId);
                            successfullCdrBatch.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        // We would like to save the faile records to our repository.
                        result.FailedRecords++;
                        failedCdrBatch.Add(new FailedCdrRecord
                        {
                            UploadCorrelationId = uploadCorrelationId.ToString(),
                            RowNumberInCsv = rowNumber,
                            RawRowData = rawRow.Length > 1024 ? rawRow.Substring(0, 1024) : rawRow, // Truncate if too long
                            ErrorMessage = ex.Message.Length > 2000 ? ex.Message.Substring(0, 2000) : ex.Message, // Truncate
                            FailedAtUtc = DateTime.UtcNow
                        });

                        if (failedCdrBatch.Count >= BatchSize)
                        {
                            await _failedRecordRepository.AddBatchAsync(failedCdrBatch, cancellationToken);
                            await _dbContext.SaveChangesAsync(cancellationToken);

                            _logger.LogInformation("Saved batch of {BatchCount} Failed CDRs. Total failed so far: {TotalFailed}. UploadCorrelationId: {UploadCorrelationId}", failedCdrBatch.Count, result.FailedRecords, uploadCorrelationId);
                            failedCdrBatch.Clear();
                        }

                        result.ErrorMessages.Add($"Row {rowNumber}: {ex.Message}");
                        _logger.LogWarning(ex, "Failed to process or map record for row {RowNumber} with reference {Reference}. UploadCorrelationId: {UploadCorrelationId}. Skipping.", rowNumber, recordDto.Reference, uploadCorrelationId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error during CSV parsing for CorrelationId {CorrelationId} after row {RowNumber}.", uploadCorrelationId, rowNumber);
                result.ErrorMessages.Add($"Critical CSV parsing error: {ex.Message}");
                // Potentially mark all remaining as failed or handle as a general file failure
                // For simplicity here, we'll rely on row-level failures already captured.
                // If this exception occurs, it might mean no records were processed.
                if (!successfullCdrBatch.Any() && !failedCdrBatch.Any())
                {
                    // This means a very early error, possibly before any rows were read.
                    // We might create a single 'failed file' record if desired.
                }
            }

            if (failedCdrBatch.Count != 0)
            {
                await _failedRecordRepository.AddBatchAsync(failedCdrBatch, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Saved final batch of {BatchCount} CDRs. Total processed: {TotalFailed}. UploadCorrelationId: {UploadCorrelationId}", failedCdrBatch.Count, result.FailedRecords, uploadCorrelationId);
            }

            if (successfullCdrBatch.Count != 0)
            {
                await _cdrRepository.AddBatchAsync(successfullCdrBatch, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Saved final batch of {BatchCount} CDRs. Total processed: {TotalProcessed}. UploadCorrelationId: {UploadCorrelationId}", successfullCdrBatch.Count, result.SuccessfulRecords, uploadCorrelationId);
            }

            _logger.LogInformation("Finished processing CDR data. Total processed: {TotalProcessed}, Total failed: {TotalFailed}. UploadCorrelationId: {UploadCorrelationId}", result.SuccessfulRecords, result.FailedRecords, uploadCorrelationId);

            return result;
        }

        private async Task ValidateRowAsync(CdrFileRecordDto recordDto)
        {
            if (string.IsNullOrWhiteSpace(recordDto.Reference))
            {
                throw new InvalidDataException("Reference field is missing or empty.");
            }
            if (string.IsNullOrWhiteSpace(recordDto.CallerId))
            {
                throw new InvalidDataException("CallerId field is missing or empty.");
            }
            if (string.IsNullOrWhiteSpace(recordDto.Recipient))
            {
                throw new InvalidDataException("Recipient field is missing or empty.");
            }
            if (string.IsNullOrWhiteSpace(recordDto.CallDate) || string.IsNullOrWhiteSpace(recordDto.EndTime))
            {
                throw new InvalidDataException("CallDate or EndTime is missing.");
            }
            if (!recordDto.Duration.HasValue)
            {
                throw new InvalidDataException("Duration is missing.");
            }
            if (!recordDto.Cost.HasValue)
            {
                throw new InvalidDataException("Cost is missing.");
            }
            if (string.IsNullOrWhiteSpace(recordDto.Currency) || recordDto.Currency.Length != 3)
            {
                throw new InvalidDataException("Currency is missing or not 3 characters.");
            }
        }

        // Temporary DTO for CsvHelper mapping - still reads original CSV columns
        private class CdrFileRecordDto
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
