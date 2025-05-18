using TelecomCdr.Abstraction.Models;

namespace TelecomCdr.Abstraction.Interfaces.Service
{
    /// <summary>
    /// Interface for a strategy to split a blob into smaller chunks.
    /// Future enhancements may include different strategies for chunking.
    /// This is just a placeholder for now.
    /// </summary>
    internal interface IBlobChunkingStrategy
    {
        Task<List<ChunkInfo>> SplitAsync(
            Stream blobStream,
            long blobSize,
            string blobName,
            string containerName,
            string jobId,
            long targetChunkSizeInBytes);
    }
}
