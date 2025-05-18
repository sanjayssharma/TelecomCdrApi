using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Text;
using TelecomCdr.Core.Features.CdrProcessing.Commands;
using TelecomCdr.Core.Features.CdrProcessing.Validators;
using TelecomCdr.Core.Models;

namespace TelecomCdr.Core.UnitTests.Validators
{
    [TestFixture]
    public class CommandValidatorTests
    {
        private EnqueueCdrFileProcessingCommandValidator _enqueueValidator;
        private ProcessCdrFileCommandValidator _processValidator;
        private InitiateDirectUploadCommandValidator _initiateDirectUploadValidator;

        // Max file sizes from validators (ensure these match the constants in your validator classes)
        private const long MaxFileSizeForEnqueue = 200 * 1024 * 1024; // 200 MB
        private const long MaxFileSizeForProcess = 100 * 1024 * 1024; // 100 MB

        [SetUp]
        public void SetUp()
        {
            _enqueueValidator = new EnqueueCdrFileProcessingCommandValidator();
            _processValidator = new ProcessCdrFileCommandValidator();
            _initiateDirectUploadValidator = new InitiateDirectUploadCommandValidator();
        }

        // Helper to create a mock IFormFile
        private IFormFile CreateMockFormFile(string fileName, long length, string contentType, string content = "dummy,content")
        {
            var mockFile = new Mock<IFormFile>();
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            mockFile.Setup(f => f.FileName).Returns(fileName);
            mockFile.Setup(f => f.Length).Returns(length);
            mockFile.Setup(f => f.ContentType).Returns(contentType);
            mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
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
        public void EnqueueValidator_WhenFileIsEmpty_ShouldHaveValidationError()
        {
            var mockFile = CreateMockFormFile("test.csv", 0, "text/csv", ""); // Empty file
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage("File cannot be empty.");
        }

        [TestCase("application/pdf")]
        [TestCase("text/plain")]
        [TestCase("image/jpeg")]
        public void EnqueueValidator_WhenFileContentTypeIsInvalid_ShouldHaveValidationError(string invalidContentType)
        {
            var mockFile = CreateMockFormFile("test.doc", 1024, invalidContentType);
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage("File must be a CSV.");
        }

        [TestCase("text/csv")]
        [TestCase("application/vnd.ms-excel")]
        [TestCase("application/octet-stream")] // As per validator
        public void EnqueueValidator_WhenFileContentTypeIsValid_ShouldNotHaveContentTypeValidationError(string validContentType)
        {
            var mockFile = CreateMockFormFile("test.csv", 1024, validContentType);
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldNotHaveValidationErrorFor(x => x.File); // This will pass if other rules for File also pass
        }


        [Test]
        public void EnqueueValidator_WhenFileSizeExceedsLimit_ShouldHaveValidationError()
        {
            var oversizedFile = CreateMockFormFile("large.csv", MaxFileSizeForEnqueue + 1, "text/csv");
            var command = new EnqueueCdrFileProcessingCommand (oversizedFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage($"File size exceeds the limit of {MaxFileSizeForEnqueue / (1024 * 1024)}MB for this endpoint. Please use the direct upload mechanism for larger files.");
        }

        [Test]
        public void EnqueueValidator_WhenCorrelationIdIsEmpty_ShouldHaveValidationError()
        {
            var mockFile = CreateMockFormFile("test.csv", 1024, "text/csv");
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.Empty);
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.CorrelationId)
                  .WithErrorMessage("Correlation ID is required.");
        }

        [Test]
        public void EnqueueValidator_WhenCommandIsValid_ShouldNotHaveValidationErrors()
        {
            var mockFile = CreateMockFormFile("test.csv", 1024, "text/csv");
            var command = new EnqueueCdrFileProcessingCommand (mockFile, Guid.NewGuid());
            var result = _enqueueValidator.TestValidate(command);
            result.ShouldNotHaveAnyValidationErrors();
        }

        #endregion

        #region ProcessCdrFileCommandValidator Tests

        [Test]
        public void ProcessValidator_WhenFileIsNull_ShouldHaveValidationError()
        {
            var command = new ProcessCdrFileCommand(null, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage("File is required.");
        }

        [Test]
        public void ProcessValidator_WhenFileIsEmpty_ShouldHaveValidationError()
        {
            var mockFile = CreateMockFormFile("test.csv", 0, "text/csv", ""); // Empty file
            var command = new ProcessCdrFileCommand (mockFile, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage("File cannot be empty.");
        }

        [TestCase("application/zip")]
        [TestCase("text/xml")]
        public void ProcessValidator_WhenFileContentTypeIsInvalid_ShouldHaveValidationError(string invalidContentType)
        {
            var mockFile = CreateMockFormFile("test.zip", 1024, invalidContentType);
            var command = new ProcessCdrFileCommand (mockFile, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage("File must be a CSV.");
        }

        [TestCase("text/csv")]
        [TestCase("application/vnd.ms-excel")]
        [TestCase("application/octet-stream")]
        public void ProcessValidator_WhenFileContentTypeIsValid_ShouldNotHaveContentTypeValidationError(string validContentType)
        {
            var mockFile = CreateMockFormFile("test.csv", 1024, validContentType);
            var command = new ProcessCdrFileCommand (mockFile, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldNotHaveValidationErrorFor(x => x.File);
        }

        [Test]
        public void ProcessValidator_WhenFileSizeExceedsLimit_ShouldHaveValidationError()
        {
            // MaxFileSizeForProcess is 100MB
            var oversizedFile = CreateMockFormFile("large.csv", MaxFileSizeForProcess + 1, "text/csv");
            var command = new ProcessCdrFileCommand (oversizedFile, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.File)
                  .WithErrorMessage($"File size exceeds the limit of {MaxFileSizeForProcess / (1024 * 1024)}MB for this endpoint. Please use the direct upload mechanism for larger files.");
        }

        [Test]
        public void ProcessValidator_WhenCorrelationIdIsEmpty_ShouldHaveValidationError()
        {
            var mockFile = CreateMockFormFile("test.csv", 1024, "text/csv");
            var command = new ProcessCdrFileCommand (mockFile, Guid.Empty);
            var result = _processValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.CorrelationId)
                  .WithErrorMessage("Correlation ID is required.");
        }

        [Test]
        public void ProcessValidator_WhenCommandIsValid_ShouldNotHaveValidationErrors()
        {
            var mockFile = CreateMockFormFile("test.csv", 1024, "text/csv");
            var command = new ProcessCdrFileCommand (mockFile, Guid.NewGuid());
            var result = _processValidator.TestValidate(command);
            result.ShouldNotHaveAnyValidationErrors();
        }

        #endregion

        #region InitiateDirectUploadCommandValidator Tests

        [Test]
        public void InitiateDirectUploadValidator_WhenUploadRequestIsNull_ShouldHaveValidationError()
        {
            var command = new InitiateDirectUploadCommand(null);
            var result = _initiateDirectUploadValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.UploadRequest)
                  .WithErrorMessage("Upload request payload cannot be empty or null.");
        }

        [Test]
        public void InitiateDirectUploadValidator_WhenUploadRequestFileNameIsEmpty_ShouldHaveValidationError()
        {
            var uploadRequestDto = new InitiateUploadRequestDto { FileName = "", ContentType = "text/csv" };
            var command = new InitiateDirectUploadCommand(uploadRequestDto);
            var result = _initiateDirectUploadValidator.TestValidate(command);

            // The error message is for the UploadRequest property due to the chained .Must()
            result.ShouldHaveValidationErrorFor(x => x.UploadRequest)
                  .WithErrorMessage("Name of the file to be uploaded cannot be null or empty");
        }

        [Test]
        public void InitiateDirectUploadValidator_WhenUploadRequestFileNameIsNull_ShouldHaveValidationError()
        {
            var uploadRequestDto = new InitiateUploadRequestDto { FileName = null, ContentType = "text/csv" };
            var command = new InitiateDirectUploadCommand(uploadRequestDto);
            var result = _initiateDirectUploadValidator.TestValidate(command);
            result.ShouldHaveValidationErrorFor(x => x.UploadRequest)
                  .WithErrorMessage("Name of the file to be uploaded cannot be null or empty");
        }

        [TestCase("application/pdf")]
        [TestCase("text/plain")]
        [TestCase("image/jpeg")]
        public void InitiateDirectUploadValidator_WhenContentTypeIsInvalid_ShouldHaveValidationError(string invalidContentType)
        {
            var uploadRequestDto = new InitiateUploadRequestDto { FileName = "test.csv", ContentType = invalidContentType };
            var command = new InitiateDirectUploadCommand(uploadRequestDto);
            var result = _initiateDirectUploadValidator.TestValidate(command);

            // The error message is for the UploadRequest property due to the chained .Must()
            result.ShouldHaveValidationErrorFor(x => x.UploadRequest)
                  .WithErrorMessage("File must be a CSV.");
        }

        [TestCase("text/csv")]
        [TestCase("application/vnd.ms-excel")]
        [TestCase("application/octet-stream")]
        public void InitiateDirectUploadValidator_WhenContentTypeIsValid_ShouldNotHaveContentTypeValidationError(string validContentType)
        {
            var uploadRequestDto = new InitiateUploadRequestDto { FileName = "test.csv", ContentType = validContentType };
            var command = new InitiateDirectUploadCommand(uploadRequestDto);
            var result = _initiateDirectUploadValidator.TestValidate(command);

            // Check that the specific error message for content type is not present
            var contentTypeError = result.Errors.FirstOrDefault(e => e.PropertyName == nameof(command.UploadRequest) && e.ErrorMessage == "File must be a CSV.");
            Assert.IsNull(contentTypeError, "Should not have 'File must be a CSV.' error for valid content type.");
        }

        [Test]
        public void InitiateDirectUploadValidator_WhenUploadRequestIsValid_ShouldNotHaveValidationErrors()
        {
            var uploadRequestDto = new InitiateUploadRequestDto { FileName = "test.csv", ContentType = "text/csv" };
            var command = new InitiateDirectUploadCommand(uploadRequestDto);
            var result = _initiateDirectUploadValidator.TestValidate(command);
            result.ShouldNotHaveAnyValidationErrors();
        }

        #endregion
    }
}
