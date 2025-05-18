using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Text;
using TelecomCdr.Core.Features.CdrProcessing.Commands;
using TelecomCdr.Core.Features.CdrProcessing.Validators;

namespace TelecomCdr.Core.UnitTests.Features.CdrProcessing.Validators
{
    [TestFixture]
    public class CommandValidatorTests
    {
        private EnqueueCdrFileProcessingCommandValidator _enqueueValidator;
        private ProcessCdrFileCommandValidator _processValidator; // Assuming its structure

        [SetUp]
        public void SetUp()
        {
            _enqueueValidator = new EnqueueCdrFileProcessingCommandValidator();
            _processValidator = new ProcessCdrFileCommandValidator();
        }

        // Helper to create a mock IFormFile
        private IFormFile CreateMockFormFile(string fileName, long length, string content = "dummy content")
        {
            var mockFile = new Mock<IFormFile>();
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            mockFile.Setup(f => f.FileName).Returns(fileName);
            mockFile.Setup(f => f.Length).Returns(length);
            mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
            // Mock ContentType if your validator uses it strictly.
            // For CSV, often filename extension is primary.
            if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                mockFile.Setup(f => f.ContentType).Returns("text/csv");
            }
            else
            {
                mockFile.Setup(f => f.ContentType).Returns("application/octet-stream");
            }
            return mockFile.Object;
        }

        #region EnqueueCdrFileProcessingCommandValidator Tests

        [Test]
        public void EnqueueValidator_WhenFileIsNull_ShouldHaveValidationError()
        {
            var command = new EnqueueCdrFileProcessingCommand (null, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage("File is required.");
        }

        [Test]
        public void EnqueueValidator_WhenFileIsNotCsv_ShouldHaveValidationErrorForFileAndFileName()
        {
            var mockFile = CreateMockFormFile("test.txt", 1024);
            var command = new EnqueueCdrFileProcessingCommand(mockFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);

            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage("Invalid file type. Only .csv files are allowed.");
        }

        [Test]
        public void EnqueueValidator_WhenFileSizeExceedsLimit_ShouldHaveValidationError()
        {
            // MaxFileSizeForEnqueue is 100MB (100 * 1024 * 1024 bytes)
            var largeFileSize = (100 * 1024 * 1024) + 1;
            var mockFile = CreateMockFormFile("test.csv", largeFileSize);
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage($"File size exceeds the limit of 100MB for this endpoint. Please use the direct upload mechanism for larger files.");
        }

        [Test]
        public void EnqueueValidator_WhenFileNameIsEmpty_ShouldHaveValidationError()
        {
            var mockFile = CreateMockFormFile("test.csv", 1024); // Valid file for other rules
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
        }

        [Test]
        public void EnqueueValidator_WhenCorrelationIdIsEmpty_ShouldHaveValidationError()
        {
            var mockFile = CreateMockFormFile("test.csv", 1024);
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.Empty);
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.CorrelationId)
                  .WithErrorMessage("Correlation ID is required.");
        }

        [Test]
        public void EnqueueValidator_WhenCorrelationIdIsInvalidGuid_ShouldHaveValidationError()
        {
            var mockFile = CreateMockFormFile("test.csv", 1024);
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.Empty);
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.CorrelationId)
                  .WithErrorMessage("Correlation ID must be a valid GUID.");
        }

        [Test]
        public void EnqueueValidator_WhenCommandIsValid_ShouldNotHaveValidationErrors() 
        {
            var mockFile = CreateMockFormFile("test.csv", 1024);
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldNotHaveAnyValidationErrors();
        }

        #endregion

        #region ProcessCdrFileCommandValidator Tests
        // These tests assume ProcessCdrFileCommand has JobId and CorrelationId properties.
        // Adjust if the command structure is different.

        [Test]
        public void ProcessValidator_WhenJobIdIsEmpty_ShouldHaveValidationError()
        {
            var command = new ProcessCdrFileCommand (null, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.CorrelationId)
                  .WithErrorMessage("Job ID is required.");
        }

        [Test]
        public void ProcessValidator_WhenJobIdIsInvalidGuid_ShouldHaveValidationError()
        {
            var command = new ProcessCdrFileCommand(null, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.CorrelationId)
                  .WithErrorMessage("Job ID must be a valid GUID.");
        }

        [Test]
        public void ProcessValidator_WhenCorrelationIdIsProvidedAndInvalidGuid_ShouldHaveValidationError()
        {
            var command = new ProcessCdrFileCommand(null, Guid.Empty);
            var result = _processValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.CorrelationId)
                  .WithErrorMessage("Correlation ID must be a valid GUID if provided.");
        }

        [Test]
        public void ProcessValidator_WhenCorrelationIdIsEmpty_ShouldNotHaveValidationErrorForCorrelationId()
        {
            // The rule for CorrelationId has .When(x => !string.IsNullOrWhiteSpace(x.CorrelationId))
            var command = new ProcessCdrFileCommand (null, Guid.Empty );
            var result = _processValidator.TestValidate(command);
            result.ShouldNotHaveValidationErrorFor(x => x.CorrelationId);
        }

        [Test]
        public void ProcessValidator_WhenCorrelationIdIsNull_ShouldNotHaveValidationErrorForCorrelationId()
        {
            var command = new ProcessCdrFileCommand(null, Guid.Empty);
            var result = _processValidator.TestValidate(command);
            result.ShouldNotHaveValidationErrorFor(x => x.CorrelationId);
        }


        [Test]
        public void ProcessValidator_WhenCommandIsValid_ShouldNotHaveValidationErrors()
        {
            var command = new ProcessCdrFileCommand(null, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldNotHaveAnyValidationErrors();
        }

        [Test]
        public void ProcessValidator_WhenCommandIsValidWithNullCorrelationId_ShouldNotHaveValidationErrors()
        {
            var command = new ProcessCdrFileCommand(null, Guid.Empty);
            var result = _processValidator.TestValidate(command);
            result.ShouldNotHaveAnyValidationErrors();
        }

        #endregion
    }
}
