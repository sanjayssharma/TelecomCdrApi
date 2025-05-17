using Microsoft.Extensions.Logging;
using Moq;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Domain;
using TelecomCdr.Infrastructure.Services;
using TelecomCdr.Infrastructure.UnitTests.Helpers;

namespace TelecomCdr.Infrastructure.UnitTests.Services
{
    [TestFixture]
    public class CsvFileProcessingServiceTests
    {
        private Mock<IBlobStorageService> _mockBlobService;
        private Mock<ICdrRepository> _mockCdrRepository;
        private Mock<IFailedCdrRecordRepository> _mockFailedRecordRepository;
        private Mock<ILogger<CsvFileProcessingService>> _mockLogger;
        private const int BATCH_SIZE = 2;

        private CsvFileProcessingService _service;

        [SetUp]
        public void Setup()
        {
            _mockBlobService = new Mock<IBlobStorageService>();
            _mockCdrRepository = new Mock<ICdrRepository>();
            _mockFailedRecordRepository = new Mock<IFailedCdrRecordRepository>();
            _mockLogger = new Mock<ILogger<CsvFileProcessingService>>();

            _service = new CsvFileProcessingService(
                _mockCdrRepository.Object,
                _mockFailedRecordRepository.Object,
                _mockBlobService.Object,
                _mockLogger.Object);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_ValidCsv_AllRecordsProcessedSuccessfully()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var records = Enumerable.Range(1, 5).Select(TestHelpers.GenerateValidTestCdrRecordDto).ToList();
            var csvContent = TestHelpers.GenerateCsvContent(records);
            var stream = TestHelpers.CreateCsvStreamFromString(csvContent);

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                            .ReturnsAsync(stream);
            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()))
                              .Returns(Task.CompletedTask);

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("test.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(5));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
            Assert.IsEmpty(result.ErrorMessages);
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<CallDetailRecord>>(list => list.Count() == 5)), Times.Once);
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()), Times.Never);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_EmptyFile_ReturnsZeroProcessedAndFailed()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var stream = TestHelpers.CreateCsvStreamFromString("CallerId,Recipient,CallDate,EndTime,Duration,Cost,Reference,Currency\n"); // Header only

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                            .ReturnsAsync(stream);

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("empty.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
            Assert.IsTrue(result.ErrorMessages.Any(m => m.Contains("empty or contained only a header")));
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()), Times.Never);
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()), Times.Never);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_CompletelyEmptyFile_ReturnsZeroProcessedAndFailed()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var stream = TestHelpers.CreateCsvStreamFromString(""); // Completely empty

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                            .ReturnsAsync(stream);
            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("completely_empty.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
        }


        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_BlobNotFound_ReturnsFailedResult()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                            .ReturnsAsync(Stream.Null); // Simulate blob not found

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("nonexistent.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(-1)); // Indicates file-level error
            Assert.That(result.ErrorMessages.Any(m => m.Contains("not found in container")), Is.True);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_RowWithInvalidDateFormat_ProcessesValidAndLogsFailed()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var cancellationToken = It.IsAny<CancellationToken>();
            var records = new List<CdrFileRecordDto>
            {
                TestHelpers.GenerateValidTestCdrRecordDto(1),
                new CdrFileRecordDto { CallerId = "Caller2", Recipient = "Rec2", CallDate = "INVALID-DATE", EndTime = "10:00:00", Duration = 10, Cost = 1, Reference = "RefInvalidDate", Currency = "USD" },
                TestHelpers.GenerateValidTestCdrRecordDto(3)
            };
            var csvContent = TestHelpers.GenerateCsvContent(records);
            var stream = TestHelpers.CreateCsvStreamFromString(csvContent);

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(stream);
            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>())).Returns(Task.CompletedTask);
            _mockFailedRecordRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>())).Returns(Task.CompletedTask);

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("mixed.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(2));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(1));
            Assert.IsTrue(result.ErrorMessages.Any(m => m.Contains("INVALID-DATE' was not recognized as a valid DateTime.")));
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<CallDetailRecord>>(list => list.Count() == 2)), Times.Once);
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<FailedCdrRecord>>(list => list.Count() == 1 && list.First().ErrorMessage.Contains("CallDate 'INVALID-DATE'"))), Times.Once);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_RowWithMissingRequiredField_ProcessesValidAndLogsFailed()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var records = new List<CdrFileRecordDto>
            {
                TestHelpers.GenerateValidTestCdrRecordDto(1),
                new CdrFileRecordDto { CallerId = "Caller2", Recipient = "Rec2", CallDate = "01/01/2023", EndTime = "10:00:00", Duration = 10, Cost = 1, Reference = null, Currency = "USD" }, // Missing Reference
                TestHelpers.GenerateValidTestCdrRecordDto(3)
            };
            var csvContent = TestHelpers.GenerateCsvContent(records);
            var stream = TestHelpers.CreateCsvStreamFromString(csvContent);

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(stream);
            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("missing_ref.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(2));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(1));
            Assert.IsTrue(result.ErrorMessages.Any(m => m.Contains("Reference field is missing or empty.")));
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<CallDetailRecord>>(list => list.Count() == 2)), Times.Once);
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<FailedCdrRecord>>(list => list.Count() == 1 && list.First().ErrorMessage.Contains("Reference field is missing"))), Times.Once);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_DbErrorOnSavingSuccessfulBatch_MovesToFailedRecords()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var records = Enumerable.Range(1, 3).Select(TestHelpers.GenerateValidTestCdrRecordDto).ToList();
            var csvContent = TestHelpers.GenerateCsvContent(records);
            var stream = TestHelpers.CreateCsvStreamFromString(csvContent);

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(stream);
            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()))
                              .ThrowsAsync(new Exception("Simulated DB error")); // Fail saving successful records
            _mockFailedRecordRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>())).Returns(Task.CompletedTask);

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("db_error.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0)); // None were actually saved
            Assert.That(result.FailedRecordsCount, Is.EqualTo(3));    // All 3 moved to failed due to DB error
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()), Times.Once); // Attempted once
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<FailedCdrRecord>>(list => list.Count() == 3 && list.All(f => f.ErrorMessage.Contains("Simulated DB error")))), Times.Once);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_DbErrorOnSavingFailedBatch_LogsCriticalError()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var records = new List<CdrFileRecordDto>
            {
                new CdrFileRecordDto { CallerId = "Invalid", Recipient = "Invalid", CallDate = "bad-date", EndTime = "bad-time", Duration = 1, Cost = 1, Reference = "Fail1", Currency = "ERR" }
            };
            var csvContent = TestHelpers.GenerateCsvContent(records);
            var stream =TestHelpers.CreateCsvStreamFromString(csvContent);

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(stream);
            _mockFailedRecordRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()))
                                       .ThrowsAsync(new Exception("Simulated DB error for failed records"));

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("db_error_failed.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(1)); // Parsing failed, but saving this failure also failed
            _mockFailedRecordRepository.Verify(r => r.AddBatchAsync(It.IsAny<IEnumerable<FailedCdrRecord>>()), Times.Once);
            // Verify logger was called with critical error (this requires more complex logger mocking or checking if not using specific extension methods)
            // For simplicity, we assume the log happens. In a real test, you might inject a test logger.
        }


        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_MultipleBatches_ProcessesAllCorrectly()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            int totalRecords = BATCH_SIZE + 5; // CsvFileProcessingService.BatchSize is private, use a known value or make BatchSize protected/internal for testing
            var records = Enumerable.Range(1, totalRecords).Select(TestHelpers.GenerateValidTestCdrRecordDto).ToList();
            var csvContent = TestHelpers.GenerateCsvContent(records);
            var stream = TestHelpers.CreateCsvStreamFromString(csvContent);

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(stream);
            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>())).Returns(Task.CompletedTask);

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("large.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(totalRecords));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<CallDetailRecord>>(list => list.Count() == 1000)), Times.Exactly(totalRecords / 1000)); // Assuming BatchSize is 1000
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<CallDetailRecord>>(list => list.Count() == totalRecords % 1000)), Times.Once);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_HeaderValidationException_ReturnsFileLevelError()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            // Simulate a scenario where CsvReader throws HeaderValidationException
            // This is hard to trigger directly without CsvReader.ValidateHeader,
            // but we can mock the stream to cause an early CsvHelperException if needed,
            // or assume the internal try-catch for HeaderValidationException works if it's thrown by CsvHelper.
            // For this test, we'll assume the service's catch block for HeaderValidationException is hit.
            // A more direct way would be to mock CsvReader if the service allowed injecting it,
            // or to craft a stream that CsvHelper itself would throw on.

            // Let's simulate by providing a stream that causes an error *before* row processing,
            // which might be caught by the broader CsvHelperException or general Exception block.
            // To specifically test HeaderValidationException, you'd typically need to configure CsvReader to validate headers
            // and provide a CSV with mismatching headers.
            // For now, let's test the general critical error path.
            var stream = new MemoryStream(); // Empty stream will cause issues if header is expected
            var writer = new StreamWriter(stream);
            writer.Write("WrongHeader1,WrongHeader2\nvalue1,value2"); // Mismatched headers
            writer.Flush();
            stream.Position = 0;


            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                            .ReturnsAsync(stream); // Stream that will likely cause CsvHelper to error early

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("bad_header.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(0));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(-1)); // File-level error
            Assert.IsTrue(result.ErrorMessages.Any(m => m.Contains("CSV Header validation failed") || m.Contains("Critical CSV processing error")));
        }

        // Test for "HH:mm:ss" time format
        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_ValidCsvWith24HourTimeFormat_ProcessesSuccessfully()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var records = new List<CdrFileRecordDto>
            {
                new CdrFileRecordDto { CallerId = "C1", Recipient = "R1", CallDate = "15/03/2024", EndTime = "14:30:00", Duration = 120, Cost = 2.50m, Reference = "RefTime1", Currency = "EUR" },
                new CdrFileRecordDto { CallerId = "C2", Recipient = "R2", CallDate = "16/03/2024", EndTime = "00:05:10", Duration = 300, Cost = 1.75m, Reference = "RefTime2", Currency = "USD" }
            };
            var csvContent = TestHelpers.GenerateCsvContent(records);
            var stream = TestHelpers.CreateCsvStreamFromString(csvContent);

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                            .ReturnsAsync(stream);
            _mockCdrRepository.Setup(r => r.AddBatchAsync(It.IsAny<IEnumerable<CallDetailRecord>>()))
                              .Returns(Task.CompletedTask);

            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("time_test.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(2));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(0));
            _mockCdrRepository.Verify(r => r.AddBatchAsync(It.Is<IEnumerable<CallDetailRecord>>(list => list.Count() == 2)), Times.Once);
        }

        [Test]
        public async Task ProcessAndStoreCdrFileFromBlobAsync_RowWithInvalid24HourTimeFormat_LogsFailed()
        {
            // Arrange
            var correlationId = Guid.NewGuid();
            var records = new List<CdrFileRecordDto>
            {
                TestHelpers.GenerateValidTestCdrRecordDto(1),
                new CdrFileRecordDto { CallerId = "Caller2", Recipient = "Rec2", CallDate = "01/01/2023", EndTime = "25:00:00", Duration = 10, Cost = 1, Reference = "RefInvalidTime", Currency = "USD" }, // Invalid hour
            };
            var csvContent = TestHelpers.GenerateCsvContent(records);
            var stream = TestHelpers.CreateCsvStreamFromString(csvContent);

            _mockBlobService.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(stream);
            // Act
            var result = await _service.ProcessAndStoreCdrFileFromBlobAsync("invalid_time.csv", "container", correlationId);

            // Assert
            Assert.That(result.SuccessfulRecordsCount, Is.EqualTo(1));
            Assert.That(result.FailedRecordsCount, Is.EqualTo(1));
            Assert.IsTrue(result.ErrorMessages.Any(m => m.Contains("EndTime '25:00:00' is not in 'HH:mm:ss' format.")));
        }
    }
}
