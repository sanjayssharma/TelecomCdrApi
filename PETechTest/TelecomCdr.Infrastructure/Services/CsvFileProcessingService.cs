using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Models;
using TelecomCdr.Domain;

namespace TelecomCdr.Infrastructure.Services
{
    public class CsvFileProcessingService : IFileProcessingService
    {
        private readonly ICdrRepository _cdrRepository;
        private readonly IFailedCdrRecordRepository _failedRecordRepository;
        private readonly IBlobStorageService _blobStorageService;
        private readonly ILogger<CsvFileProcessingService> _logger;
        private const int BatchSize = 1000;

        public CsvFileProcessingService(
            ICdrRepository cdrRepository,
            IFailedCdrRecordRepository failedRecordRepository,
            IBlobStorageService blobStorageService,
            ILogger<CsvFileProcessingService> logger)
        {
            _cdrRepository = cdrRepository ?? throw new ArgumentNullException(nameof(cdrRepository));
            _failedRecordRepository = failedRecordRepository ?? throw new ArgumentNullException(nameof(failedRecordRepository));
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
            _logger.LogInformation("Processing and storing CDR data from blob: {ContainerName}/{BlobName}. UploadCorrelationId: {UploadCorrelationId}",
                containerName, blobName, originalUploadCorrelationId);

            var result = new FileProcessingResult();

            try
            {
                // *** CRITICAL: DownloadFileAsync should return a stream that can be read progressively ***
                // Azure SDK's DownloadStreamingAsync or DownloadContentAsync().Value.Content is suitable here.
                using Stream blobStream = await _blobStorageService.DownloadFileAsync(containerName, blobName);
                if (blobStream == null || blobStream == Stream.Null)
                {
                    _logger.LogError("Blob {BlobName} from container {ContainerName} not found or is empty.", blobName, containerName);

                    result.FailedRecordsCount = -1; // Indicate a file-level failure
                    result.ErrorMessages.Add($"Blob {blobName} not found in container {containerName} or is empty.");
                    // No records processed or failed if file is missing/empty
                    return result; // Or throw an exception FileNotFoundException
                }
                return await ProcessInternalAsync(blobStream, originalUploadCorrelationId, cancellationToken);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download blob {BlobName} from container {ContainerName} for processing. UploadCorrelationId: {UploadCorrelationId}",
                    blobName, containerName, originalUploadCorrelationId);

                result.ErrorMessages.Add($"Exception ocurred when trying to access the file: {blobName}, uploadCorrelationId: {originalUploadCorrelationId}");
                return result;
            }
        }

        private async Task<FileProcessingResult> ProcessInternalAsync(Stream blobStream, Guid uploadCorrelationId, CancellationToken cancellationToken)
        {
            var result = new FileProcessingResult();
            var successfullCdrBatch = new List<CallDetailRecord>();
            var failedCdrBatch = new List<FailedCdrRecord>();
            var correlationIdString = uploadCorrelationId.ToString();

            try
            {
                var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    MissingFieldFound = null,
                    HeaderValidated = null,
                    TrimOptions = TrimOptions.Trim,
                    BadDataFound = context => // Handle bad data per row
                    {
                        _logger.LogWarning("Bad data found in CSV row: {RawRow}. Field: {Field}, Context: {Context}", 
                            context.RawRecord, context.Field, context.Context);
                    }
                };

                // For error reporting, assuming header is row 1
                int rowNumber = 1;

                using var reader = new StreamReader(blobStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                using var csv = new CsvReader(reader, csvConfig);
                csv.Context.RegisterClassMap<CdrFileRecordDtoMap>(); // Ensure DTO mapping is used

                // Manually read and validate header if CsvReader's default isn't sufficient or to provide custom error
                var validationResult = await ValidateCsvHeaderAsync(csv, correlationIdString, cancellationToken);

                if (validationResult != null)
                {
                    result.ErrorMessages.AddRange(validationResult.ErrorMessages);
                    result.FailedRecordsCount = -1; // Indicate a file-level failure
                    return result;
                }

                // Process row by row
                while (await csv.ReadAsync())
                {
                    rowNumber++;
                    string rawRow = csv.Context.Parser?.RawRecord ?? string.Empty;
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    try
                    {
                        var recordDto = csv.GetRecord<CdrFileRecordDto>();

                        // Validate the row and adjust the rules accordingly. So invalid data could be identified and recorded.
                        await ValidateRowAsync(recordDto);

                        // Fields have been validated so we could safely parse them
                        var callDatePart = DateTime.ParseExact(recordDto.CallDate, "dd/MM/yyyy", CultureInfo.InvariantCulture);
                        var endTimePart = TimeSpan.Parse(recordDto.EndTime);
                        var duration = int.Parse(recordDto.Duration, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        var cost = decimal.Parse(recordDto.Cost, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

                        var cdr = new CallDetailRecord(
                            recordDto.CallerId, recordDto.Recipient, callDatePart, endTimePart, duration,
                            cost, recordDto.Reference, recordDto.Currency, uploadCorrelationId
                        );
                        successfullCdrBatch.Add(cdr);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process CSV row {RowNumber} for CorrelationId {CorrelationId}. Raw: '{RawData}'. Error: {Error}",
                        rowNumber, correlationIdString, rawRow.Length > 200 ? rawRow.Substring(0, 200) + "..." : rawRow, ex.Message);

                        failedCdrBatch.Add(new FailedCdrRecord
                        {
                            UploadCorrelationId = uploadCorrelationId.ToString(),
                            RowNumberInCsv = rowNumber,
                            RawRowData = rawRow.Length > 1024 ? rawRow.Substring(0, 1024) : rawRow,
                            ErrorMessage = ex.Message.Length > 2000 ? ex.Message.Substring(0, 2000) : ex.Message,
                            FailedAtUtc = DateTime.UtcNow
                        });

                        result.FailedRecordsCount++;
                        result.ErrorMessages.Add($"Row {rowNumber}: {ex.Message}");
                    }

                    // Process batches to avoid holding too many records in memory
                    if (successfullCdrBatch.Count >= BatchSize)
                    {
                        await SaveSuccessfulBatchAsync(successfullCdrBatch, result, correlationIdString, failedCdrBatch);
                    }
                    if (failedCdrBatch.Count >= BatchSize)
                    {
                        await SaveFailedBatchAsync(failedCdrBatch, correlationIdString);
                    }
                }

                // Save any remaining records in the last batches
                if (successfullCdrBatch.Count != 0)
                {
                    await SaveSuccessfulBatchAsync(successfullCdrBatch, result, correlationIdString, failedCdrBatch);
                }
                if (failedCdrBatch.Count != 0)
                {
                    await SaveFailedBatchAsync(failedCdrBatch, correlationIdString);
                }
            }
            catch (HeaderValidationException headerEx)
            {
                _logger.LogError(headerEx, "CSV Header validation failed for CorrelationId {CorrelationId}. Invalid headers: {InvalidHeaders}",
                    correlationIdString, string.Join(", ", headerEx.InvalidHeaders?.Select(h => h.Names.FirstOrDefault()) ?? Enumerable.Empty<string>()));

                result.ErrorMessages.Add($"CSV Header validation failed: {headerEx.Message}");
                result.FailedRecordsCount = -1; // Indicate a file-level failure
            }
            catch (Exception ex) // Catch broader CSV parsing errors (e.g., stream errors, CsvHelper exceptions)
            {
                _logger.LogError(ex, "Critical error during CSV stream processing for CorrelationId {CorrelationId}.", correlationIdString);

                result.ErrorMessages.Add($"Critical CSV processing error: {ex.Message}");
                result.FailedRecordsCount = -1; // Indicate a file-level failure
            }

            _logger.LogInformation("Finished processing CDR data. Total processed: {TotalProcessed}, Total failed: {TotalFailed}. UploadCorrelationId: {UploadCorrelationId}", result.SuccessfulRecordsCount, result.FailedRecordsCount, correlationIdString);

            return result;
        }

        private async Task<FileProcessingResult?> ValidateCsvHeaderAsync(CsvReader csv, string correlationId, CancellationToken cancellationToken)
        {
            FileProcessingResult? fileResultOnError = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasMoreRows = await csv.ReadAsync(); 
                if (!hasMoreRows)
                {
                    // This means the stream is empty or only contains an empty line before any header.
                    _logger.LogError("CSV stream is empty or does not contain a header row for CorrelationId {CorrelationId}.", correlationId);
                    fileResultOnError = new FileProcessingResult();
                    fileResultOnError.ErrorMessages.Add("Critical error reading CSV header: Stream is empty or no header row found.");
                    return fileResultOnError;
                }

                csv.ReadHeader();      // Process the header
                csv.ValidateHeader<CdrFileRecordDto>(); // Validate based on map
            }
            catch (HeaderValidationException headerEx)
            {
                _logger.LogError(headerEx, "CSV Header validation failed for CorrelationId {CorrelationId}. Invalid headers: {InvalidHeaders}",
                    correlationId, string.Join(", ", headerEx.InvalidHeaders?.SelectMany(h => h.Names) ?? Enumerable.Empty<string>()));
                fileResultOnError = new FileProcessingResult();
                fileResultOnError.ErrorMessages.Add($"CSV Header validation failed: {headerEx.Message}");
            }
            catch (OperationCanceledException opEx)
            {
                _logger.LogWarning(opEx, "Header validation cancelled for CorrelationId {CorrelationId}.", correlationId);
                throw; // Rethrow cancellation
            }
            catch (Exception ex) // Catch other issues during header read (e.g., general stream errors, unexpected format)
            {
                _logger.LogError(ex, "Critical error reading CSV header for CorrelationId {CorrelationId}.", correlationId);
                fileResultOnError = new FileProcessingResult();
                fileResultOnError.ErrorMessages.Add($"Critical error reading CSV header: {ex.Message}");
            }

            return fileResultOnError;
        }

        private async Task SaveSuccessfulBatchAsync(List<CallDetailRecord> batch, FileProcessingResult overallResult, string correlationId, List<FailedCdrRecord> failedBatchBuffer)
        {
            if (batch.Count == 0) 
                return;

            try
            {
                await _cdrRepository.AddBatchAsync(batch);
                overallResult.SuccessfulRecordsCount += batch.Count;
                _logger.LogDebug("Saved batch of {Count} successful CDR records for CorrelationId {CorrelationId}.", batch.Count, correlationId);
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "Database error saving batch of {Count} successful CDR records for CorrelationId {CorrelationId}. Moving to failed records.", batch.Count, correlationId);
                overallResult.FailedRecordsCount += batch.Count;
                foreach (var record in batch)
                {
                    var failedMessage = new FailedCdrRecord // Add to the main failed batch for later saving
                    {
                        UploadCorrelationId = correlationId,
                        RawRowData = $"Ref: {record.Reference}, Caller: {record.CallerId}", // Or serialize
                        ErrorMessage = $"DB insert failed after parse: {dbEx.Message}",
                        FailedAtUtc = DateTime.UtcNow
                    };
                    failedBatchBuffer.Add(failedMessage);

                    overallResult.ErrorMessages.Add($"{failedMessage.RawRowData}, {failedMessage.ErrorMessage}");
                }
            }
            finally
            {
                batch.Clear(); // Clear batch for next set of records
            }
        }

        private async Task SaveFailedBatchAsync(List<FailedCdrRecord> batch, string correlationId)
        {
            if (batch.Count == 0) 
                return;

            try
            {
                await _failedRecordRepository.AddBatchAsync(batch);
                _logger.LogDebug("Saved batch of {Count} failed CSV row records for CorrelationId {CorrelationId}.", batch.Count, correlationId);
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "Database error saving batch of {Count} failed CSV row records for CorrelationId {CorrelationId}. These failures might be lost.", batch.Count, correlationId);
                // Potentially log these to a fallback mechanism if DB for failed records is also failing
            }
            finally
            {
                batch.Clear(); // Clear batch
            }
        }

        private async Task ValidateRowAsync(CdrFileRecordDto recordDto)
        {
            if (string.IsNullOrWhiteSpace(recordDto.Reference)) 
                throw new InvalidDataException("Reference field is missing or empty.");
            
            if (string.IsNullOrWhiteSpace(recordDto.CallerId)) 
                throw new InvalidDataException("CallerId field is missing or empty.");
            
            if (string.IsNullOrWhiteSpace(recordDto.Recipient)) 
                throw new InvalidDataException("Recipient field is missing or empty.");
            
            if (string.IsNullOrWhiteSpace(recordDto.Currency) || recordDto.Currency.Length != 3)
                throw new InvalidDataException("Currency field is missing or not 3 characters.");

            if (string.IsNullOrWhiteSpace(recordDto.Duration)) 
                throw new InvalidDataException("Duration field is missing or empty.");
            if (!int.TryParse(recordDto.Duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out int durationValue))
                throw new InvalidDataException($"Invalid format for Duration: '{recordDto.Duration}'. Expected an integer.");

            if (string.IsNullOrWhiteSpace(recordDto.Cost)) 
                throw new InvalidDataException("Cost field is missing or empty.");

            // Use NumberStyles.AllowDecimalPoint for regions that use comma as decimal separator if InvariantCulture doesn't cover it.
            // CultureInfo.InvariantCulture typically expects '.' as decimal separator.
            if (!decimal.TryParse(recordDto.Cost, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal costValue))
                throw new InvalidDataException($"Invalid format for Cost: '{recordDto.Cost}'. Expected a decimal number.");

            if (string.IsNullOrWhiteSpace(recordDto.CallDate) || string.IsNullOrWhiteSpace(recordDto.EndTime)) 
                throw new InvalidDataException("CallDate or EndTime is missing.");
            
            if (!DateTime.TryParseExact(recordDto.CallDate, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime callDatePart))
                throw new InvalidDataException($"Invalid format for CallDate: '{recordDto.CallDate}'. Expected dd/MM/yyyy.");

            if (!TimeSpan.TryParse(recordDto.EndTime, CultureInfo.InvariantCulture, out TimeSpan endTimePart))
                throw new InvalidDataException($"Invalid format for EndTime: '{recordDto.EndTime}'. Expected HH:mm:ss.");
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
