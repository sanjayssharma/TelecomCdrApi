using FluentValidation;
using TelecomCdr.Core.Features.CdrProcessing.Commands;

namespace TelecomCdr.Core.Features.CdrProcessing.Validators
{
    public class ProcessCdrFileCommandValidator : AbstractValidator<ProcessCdrFileCommand>
    {
        public ProcessCdrFileCommandValidator()
        {
            RuleFor(x => x.File)
                .NotNull().WithMessage("File is required.")
                .Must(file => file.Length > 0).WithMessage("File cannot be empty.")
                .Must(file => file.ContentType == "text/csv" || file.ContentType == "application/vnd.ms-excel" || file.ContentType == "application/octet-stream")
                .WithMessage("File must be a CSV.");

            RuleFor(x => x.CorrelationId)
                .NotEmpty().WithMessage("Correlation ID is required.");
        }
    }
}
