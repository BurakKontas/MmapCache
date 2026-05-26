using System.IO.MemoryMappedFiles;

namespace MmapCache.Lsm;

/// <summary>
/// Represents a static, immutable, durable on-disk data file (SSTable).
/// This implementation relies on memory-mapped files (MMF) directly handled 
/// via unmanaged pointers for ultimate zero-copy reads, eliminating GC heap overhead.
/// </summary>
public class Segment : IDisposable
{
    public int SegmentId { get; }
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly unsafe byte* _basePtr;

    public unsafe Segment(int segmentId, string path)
    {
        SegmentId = segmentId;
        
        long fileLength = new FileInfo(path).Length;
        // Map as read-only
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, fileLength, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);
        
        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _basePtr = ptr;
    }

    /// <summary>
    /// Reads a memory segment directly utilizing the unmanaged base pointer of the memory-mapped file.
    /// This achieves true zero-copy retrieval for blazing fast reads.
    /// </summary>
    public unsafe ReadOnlySpan<byte> ReadValue(long offset, int length)
    {
        return new ReadOnlySpan<byte>(_basePtr + offset, length);
    }
    
    // Additional methods for reading index/footer can be added here
    
    public unsafe void Dispose()
    {
        if (_basePtr != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        _accessor.Dispose();
        _mmf.Dispose();
    }
}

