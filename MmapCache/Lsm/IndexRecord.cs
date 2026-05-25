namespace MmapCache.Lsm;

public struct IndexRecord
{
    public bool IsMemTable;
    public int SegmentId;
    public long Offset;
    public int Length;
}

