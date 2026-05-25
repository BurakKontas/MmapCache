using System.IO.MemoryMappedFiles;

namespace MmapCache.Lsm;

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

