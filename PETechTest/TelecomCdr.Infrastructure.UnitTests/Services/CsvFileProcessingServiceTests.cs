using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Domain;
using TelecomCdr.Infrastructure.Services;


namespace TelecomCdr.Infrastructure.UnitTests.Services
{
    [TestFixture]
    public class CsvFileProcessingServiceTests
    {
        private Mock<ICdrRepository> _mockCdrRepository;
        private Mock<IFailedCdrRecordRepository> _mockFailedRecordRepository;
        private Mock<IBlobStorageService> _mockBlobStorageService;
        private Mock<ILogger<CsvFileProcessingService>> _mockLogger;
        private CsvFileProcessingService _csvFileProcessingService;

        private const string ValidCsvHeader = "caller_id,recipient,call_date,end_time,duration,cost,reference,currency";

        [SetUp]
        public void SetUp()
        {
            _mockCdrRepository = new Mock<ICdrRepository>();
            _mockFailedRecordRepository = new Mock<IFailedCdrRecordRepository>();
            _mockBlobStorageService = new Mock<IBlobStorageService>();
            _mockLogger = new Mock<ILogger<CsvFileProcessingService>>();

            _csvFileProcessingService = new CsvFileProcessingService(
                _mockCdrRepository.Object,
                _mockFailedRecordRepository.Object,
                _mockBlobStorageService.Object,
                _mockLogger.Object
            );
        }

        private Stream GenerateCsvStream(string content)
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(content));
        }

        #region ProcessAndStoreCdrFileAsync (and ProcessInternalAsync) Tests

        [Test]
        public async Task ProcessAndStoreCdrFileAsync_WithValidCsv_ProcessesAllRecordsSuccessfully()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = $"{ValidCsvHeader}\n" +
                             "123,456,01/01/2024,10:00:00,60,0.5,ref1,USD\n" +
                             "789,012,02/01/2024,11:00:00,120,1.0,ref2,USD";
            using var stream = GenerateCsvStream(csvContent);

            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()))
                              .Returns(Task.CompletedTask);

            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(2));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
            Assert.IsEmpty(result.ErrorMessages);
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()), Times.Once);
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()), Times.Never);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileAsync_WithSomeInvalidRows_ProcessesValidAndCapturesFailed()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = $"{ValidCsvHeader}\n" +
                             "123,456,01/01/2024,10:00:00,60,0.5,ref1,USD\n" + // Valid
                             ",,invalid_date,invalid_time,nan,nan,ref2_invalid,XYZ\n" + // Invalid (missing caller, recipient, bad date/time, duration, cost)
                             "789,012,02/01/2024,11:00:00,120,1.0,ref3,USD"; // Valid
            using var stream = GenerateCsvStream(csvContent);

            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()))
                              .Returns(Task.CompletedTask);
            _mockFailedRecordRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()))
                                       .Returns(Task.CompletedTask);
            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(2));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(1));
            Assert.IsNotEmpty(result.ErrorMessages);
            Assert.IsTrue(result.ErrorMessages.Any(e => e.Contains("Row 3") && e.Contains("CallerId field is missing or empty."))); // Example error message
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()), Times.Once);
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()), Times.Once);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileAsync_WithInvalidDateFormat_CapturesFailed()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = $"{ValidCsvHeader}\n" +
                             "123,456,2024-01-01,10:00:00,60,0.5,ref1,USD"; // Invalid date format
            using var stream = GenerateCsvStream(csvContent);

            _mockFailedRecordRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()))
                                       .Returns(Task.CompletedTask);
            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(1));
            Assert.IsTrue(result.ErrorMessages.Any(e => e.Contains("Row 2") && e.Contains("String '2024-01-01' was not recognized as a valid DateTime.")));
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()), Times.Never);
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()), Times.Once);
        }


        [Test]
        public async Task ProcessAndStoreCdrFileAsync_EmptyCsv_ReturnsZeroCounts()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = ValidCsvHeader; // Only header
            using var stream = GenerateCsvStream(csvContent);

            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
            Assert.IsEmpty(result.ErrorMessages);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileAsync_CompletelyEmptyStream_ReturnsZeroCounts()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = ""; // Empty content
            using var stream = GenerateCsvStream(csvContent);

            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            // It might log a header validation error or just process nothing.
            // Based on current code, CsvReader might throw if header is expected but not found.
            // Let's check for a file-level error.
            Assert.IsTrue(result.ErrorMessages.Any(e => e.Contains("Stream is empty or no header row found")));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(-1), "FailedRecordsCount should indicate file-level error for empty stream.");
        }

        [Test]
        public async Task ProcessAndStoreCdrFileAsync_CsvHeaderValidationFails_ReturnsFileLevelError()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = "wrong_header1,wrong_header2\nval1,val2";
            using var stream = GenerateCsvStream(csvContent);

            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ProcessAndStoreCdrFileAsync_CdrDbSaveFails_MovesToFailedRecords()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = $"{ValidCsvHeader}\n123,456,01/01/2024,10:00:00,60,0.5,ref1,USD";
            using var stream = GenerateCsvStream(csvContent);
            List<FailedCdrRecord> capturedFailedList = null;

            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<List<CallDetailRecord>>()))
                              .ThrowsAsync(new Exception("Database connection error"));

            // Setup AddBatchAsync on _mockFailedRecordRepository to capture the argument
            _mockFailedRecordRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()))
                                       .Callback<IEnumerable<FailedCdrRecord>>(list => capturedFailedList = [.. list]) // Capture a copy
                                       .Returns(Task.CompletedTask);
            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId);

            // Assert
            Assert.AreEqual(0, result.SuccessfulRecordsCount, "SuccessfulRecordsCount should be 0 when DB save fails.");
            Assert.AreEqual(1, result.FailedRecordsCount, "FailedRecordsCount should be 1 when one record fails to save to CDR DB.");
            Assert.IsTrue(result.ErrorMessages.Any(e => e.Contains("DB insert failed after parse")), "ErrorMessages should contain DB insert failure message.");

            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.IsAny<List<FailedCdrRecord>>()), Times.Once, "AddBatchAsync for failed records should be called once.");

            Assert.IsNotNull(capturedFailedList, "Captured list of failed records should not be null.");
            Assert.AreEqual(1, capturedFailedList.Count, "Captured list should contain one failed record.");
            Assert.IsNotNull(capturedFailedList[0], "The failed record in the captured list should not be null.");
            StringAssert.Contains("Database connection error", capturedFailedList[0].ErrorMessage, "Failed record's ErrorMessage should contain the original DB exception message.");
            StringAssert.Contains("DB insert failed after parse", capturedFailedList[0].ErrorMessage, "Failed record's ErrorMessage should indicate it was a DB insert failure post-parse.");
        }

        [Test]
        public async Task ProcessAndStoreCdrFileAsync_FailedDbSaveAlsoFails_LogsError()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = $"{ValidCsvHeader}\n,,invalid_date,invalid_time,nan,nan,ref2_invalid,XYZ"; // This row will fail parsing
            using var stream = GenerateCsvStream(csvContent);

            _mockFailedRecordRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()))
                                       .ThrowsAsync(new Exception("Failed records DB error"));
            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(1)); // Still counts the parsing failure
            // Verify that the critical error of not being able to save failed records is logged
            VerifyLog(LogLevel.Error, msg => msg.Contains("Database error saving batch of 1 failed CSV row records") && msg.Contains("These failures might be lost"));
        }

        [Test]
        public async Task ProcessAndStoreCdrFileAsync_CancellationRequested_ThrowsOperationCanceledException()
        {
            // Arrange
            var uploadCorrelationId = Guid.NewGuid();
            var csvContent = $"{ValidCsvHeader}\n123,456,01/01/2024,10:00:00,60,0.5,ref1,USD";
            using var stream = GenerateCsvStream(csvContent);
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel(); // Cancel immediately

            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileAsync(stream, uploadCorrelationId, cancellationTokenSource.Token);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(-1));
            Assert.IsTrue(result.ErrorMessages.Any(e => e.Contains("operation was canceled")));
        }

        #endregion

        #region ProcessAndStoreCdrFileFromBlobAsync Tests

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_BlobDownloadFails_ReturnsFileLevelError()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var container = "testcontainer";
            var blob = "testblob.csv";
            _mockBlobStorageService.Setup(s => s.DownloadFileAsync(container, blob, It.IsAny<CancellationToken>()))
                                   .ReturnsAsync((Stream)null); // Simulate blob not found / error

            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileFromBlobAsync(container, blob, correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(-1));
            Assert.IsTrue(result.ErrorMessages.Any(e => e.Contains($"Blob {blob} not found")));
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_BlobDownloadThrowsException_ReturnsErrorResult()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var container = "testcontainer";
            var blob = "testblob.csv";
            var exceptionMessage = "Storage access denied";
            _mockBlobStorageService.Setup(s => s.DownloadFileAsync(container, blob, It.IsAny<CancellationToken>()))
                                   .ThrowsAsync(new Exception(exceptionMessage));

            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileFromBlobAsync(container, blob, correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
            Assert.IsTrue(result.ErrorMessages.Any(e => e.Contains($"Exception ocurred when trying to access the file: {blob}")));
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_ValidBlob_DelegatesToInternalProcessing()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var container = "testcontainer";
            var blob = "testblob.csv";
            var csvContent = $"{ValidCsvHeader}\n123,456,01/01/2024,10:00:00,60,0.5,ref1,USD";
            using var blobStream = GenerateCsvStream(csvContent);

            _mockBlobStorageService.Setup(s => s.DownloadFileAsync(container, blob, It.IsAny<CancellationToken>()))
                                   .ReturnsAsync(blobStream);
            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()))
                              .Returns(Task.CompletedTask);

            // Act
            var result = await _csvFileProcessingService.ProcessAndStoreCdrFileFromBlobAsync(container, blob, correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(1));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()), Times.Once);
        }


        #endregion

        // Helper method for verifying ILogger calls

        private void VerifyLog(LogLevel expectedLogLevel, Func<string, bool> messageStatePredicate, Times? times = null)
        {
            times ??= Times.Once();
            _mockLogger.Verify(
                x => x.Log(
                    expectedLogLevel,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((s, type) => messageStatePredicate(s.ToString())),
                    It.IsAny<Exception>(), // Allows null or any exception
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                times.Value);
        }

        // Helper for LogError calls where we want to verify exception properties
        private void VerifyLogError<TException>(
            Func<string, bool> messageStatePredicate,
            Func<TException, bool> exceptionPredicate, // Predicate for the exception itself
            Times? times = null) where TException : Exception
        {
            times ??= Times.Once();
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error, // Specific to LogError
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((s, type) => messageStatePredicate(s.ToString())),
                    It.Is<TException>(ex => exceptionPredicate(ex)), // Verify exception properties
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                times.Value);
        }
    }
}
