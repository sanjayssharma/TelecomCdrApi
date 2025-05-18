using FluentValidation;
using TelecomCdr.Core.Features.CdrProcessing.Commands;

namespace TelecomCdr.Core.Features.CdrProcessing.Validators
{
    public class InitiateDirectUploadCommandValidator : AbstractValidator<InitiateDirectUploadCommand>
    {
        public InitiateDirectUploadCommandValidator()
        {
            RuleFor(x => x.UploadRequest)
                .NotNull().WithMessage("Upload request payload cannot be empty or null.")
                .Must(uploadRequest => uploadRequest?.ContentType == "text/csv" || uploadRequest?.ContentType == "application/vnd.ms-excel" || uploadRequest?.ContentType == "application/octet-stream")
                .WithMessage("File must be a CSV.")
                .Must(uploadRequest => !string.IsNullOrEmpty(uploadRequest?.FileName))
                .WithMessage($"Name of the file to be uploaded cannot be null or empty"); ;
        }
    }
}
