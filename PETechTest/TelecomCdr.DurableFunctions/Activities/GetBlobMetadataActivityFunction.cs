using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.DurableFunctions.Dtos;

public class GetBlobMetadataActivityFunction
{
    private readonly IBlobStorageService _blobService;
    private readonly ILogger<GetBlobMetadataActivityFunction> _logger;

    public GetBlobMetadataActivityFunction(IBlobStorageService blobService, ILogger<GetBlobMetadataActivityFunction> logger)
    {
        _blobService = blobService;
        _logger = logger;
    }

    [Function(nameof(GetBlobMetadataActivityFunction))]
    public async Task<BlobMetadataResult> Run([ActivityTrigger] BlobMetadataInput input)
    {
        _logger.LogInformation("Getting metadata for blob: {ContainerName}/{BlobName}", input.ContainerName, input.BlobName);
        
        var (Size, Metadata) = await _blobService.GetBlobPropertiesAsync(input.ContainerName, input.BlobName);

        return new BlobMetadataResult 
        { 
            Size = Size, 
            Metadata = Metadata.ToDictionary() 
        };
    }
}