using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.DurableFunctions.Dtos;

namespace TelecomCdr.DurableFunctions.Activities
{
    // Note this is a sample implementation. In a real-world scenario, you would need to handle CSV parsing,
    // chunking logic, and error handling more robustly.
    // This function is responsible for splitting a large blob into smaller chunks.
    // It uses a simplified approach for demonstration purposes.
    // In a production scenario, you would need to implement robust CSV parsing and chunking logic.
    public class SplitBlobIntoChunksActivityFunction
    {
        private readonly IBlobStorageService _blobService;
        private readonly ILogger<SplitBlobIntoChunksActivityFunction> _logger;
        private const long TARGET_CHUNK_SIZE_BYTES = 200 * 1024 * 1024; // 200MB Example

        public SplitBlobIntoChunksActivityFunction(IBlobStorageService blobService, ILogger<SplitBlobIntoChunksActivityFunction> logger)
        {
            _blobService = blobService;
            _logger = logger;
        }

        [Function(nameof(SplitBlobIntoChunksActivityFunction))]
        public async Task<List<ChunkInfo>> Run([ActivityTrigger] SplitBlobInput input)
        {
            _logger.LogInformation("Splitting blob: {OriginalBlobName} for master CorrelationId: {MasterCorrelationId}",
                input.OriginalBlobName, input.MasterCorrelationId);
            var chunkInfos = new List<ChunkInfo>();
            int chunkNumber = 1;

            try
            {
                // *** THIS IS HIGHLY SIMPLIFIED - ROBUST CSV SPLITTING IS COMPLEX ***
                // You need to download, read line by line, manage headers, and upload chunks.
                // The BlobProcessingOrchestrator skeleton in a previous response has more detailed conceptual logic.
                // For brevity here, we'll just simulate creating a few chunks.

                _logger.LogWarning("SplitBlobIntoChunksActivityFunction is using SIMPLIFIED chunk creation logic for demonstration.");

                // Simulate creating 3 chunks
                for (int i = 1; i <= 3; i++)
                {
                    var chunkCorrelationId = Guid.NewGuid();
                    var chunkBlobName = $"{Path.GetFileNameWithoutExtension(input.OriginalBlobName)}_chunk{i}{Path.GetExtension(input.OriginalBlobName)}";

                    // In a real scenario, you would:
                    // 1. Read a segment of the original blob (CSV-aware)
                    // 2. Create a stream from that segment
                    // 3. Upload that stream as a new chunk blob
                    // For simulation:
                    var dummyContent = $"header1,header2\nchunk{i}_data1,chunk{i}_data2";
                    using var dummyStream = new MemoryStream(Encoding.UTF8.GetBytes(dummyContent));
                    await _blobService.UploadFileAsync(
                        input.OriginalContainer, // Or a dedicated chunks container
                        chunkBlobName,
                        dummyStream,
                        new Dictionary<string, string> {
                        { "ParentCorrelationId", input.MasterCorrelationId.ToString() },
                        { "UploadCorrelationId", chunkCorrelationId.ToString() },
                        { "ChunkNumber", i.ToString() },
                        { "IsChunk", "true" }
                        });

                    chunkInfos.Add(new ChunkInfo
                    {
                        ChunkBlobName = chunkBlobName,
                        ChunkContainerName = input.OriginalContainer,
                        ChunkCorrelationId = chunkCorrelationId,
                        ChunkNumber = i
                    });
                    _logger.LogInformation("Simulated creation of chunk {ChunkNumber}: {ChunkBlobName}", i, chunkBlobName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during blob splitting for {OriginalBlobName}", input.OriginalBlobName);
                throw; // Let Durable Functions handle activity failure
            }
            return chunkInfos;
        }
    }
}
