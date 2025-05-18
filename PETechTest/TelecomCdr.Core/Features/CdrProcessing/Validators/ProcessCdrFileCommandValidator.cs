using FluentValidation;
using Microsoft.AspNetCore.Http;
using TelecomCdr.Core.Features.CdrProcessing.Commands;

namespace TelecomCdr.Core.Features.CdrProcessing.Validators
{
    public class ProcessCdrFileCommandValidator : AbstractValidator<ProcessCdrFileCommand>
    {
        // Define a maximum file size, e.g., 100 MB for this specific endpoint.
        // Larger files should go through the direct-to-storage upload.
        private const long MaxFileSizeForEnqueue = 100 * 1024 * 1024; // 100 MB

        public ProcessCdrFileCommandValidator()
        {
            RuleFor(x => x.File)
                .NotNull().WithMessage("File is required.")
                .Must(file => file?.Length > 0).WithMessage("File cannot be empty.")
                .Must(file => file?.ContentType == "text/csv" || file?.ContentType == "application/vnd.ms-excel" || file?.ContentType == "application/octet-stream")
                .WithMessage("File must be a CSV.")
                .Must(BeWithinSizeLimit).WithMessage($"File size exceeds the limit of {MaxFileSizeForEnqueue / (1024 * 1024)}MB for this endpoint. Please use the direct upload mechanism for larger files."); ;

            RuleFor(x => x.CorrelationId)
                .NotEmpty().WithMessage("Correlation ID is required.");
        }

        private bool BeWithinSizeLimit(IFormFile file)
        {
            if (file == null) return true; // Null check handled by NotNull()
            return file.Length <= MaxFileSizeForEnqueue;
        }
    }
}
