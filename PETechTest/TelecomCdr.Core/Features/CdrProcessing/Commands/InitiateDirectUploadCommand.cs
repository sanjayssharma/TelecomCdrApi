using MediatR;
using TelecomCdr.Core.Models;

namespace TelecomCdr.Core.Features.CdrProcessing.Commands
{
    /// <summary>
    /// Command to initiate a direct-to-storage upload process.
    /// Contains the original request details from the client.
    /// </summary>
    public class InitiateDirectUploadCommand : IRequest<InitiateUploadResponseDto>
    {
        /// <summary>
        /// Details of the file to be uploaded, as provided by the client.
        /// </summary>
        public InitiateUploadRequestDto UploadRequest { get; set; }

        public InitiateDirectUploadCommand(InitiateUploadRequestDto uploadRequest)
        {
            UploadRequest = uploadRequest ?? throw new System.ArgumentNullException(nameof(uploadRequest));
        }
    }
}
