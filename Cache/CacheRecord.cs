using System.Buffers.Binary;

namespace MmapCacheApp.Cache;

/// <summary>
/// Fixed-size binary record layout (identical to mmap-cache Java format):
///
///  ┌──────────┬─────────┬──────────────────┬──────────┬──────────────────┬──────────┐
///  │  id (8B) │kLen (2B)│  key (maxKeyBytes)│vLen (2B) │ val (maxValBytes) │  ts (8B) │
///  └──────────┴─────────┴──────────────────┴──────────┴──────────────────┴──────────┘
///
/// Fixed overhead = 20 bytes.
/// Unused key/value bytes are zero-padded so every record is exactly recordSize wide.
/// Fixed stride → O(1) random access: position = offset × recordSize.
/// </summary>
internal static class CacheRecord
{
    public const int FixedOverhead = 20; // 8 id + 2 kLen + 2 vLen + 8 ts

    public static int RecordSize(int maxKeyBytes, int maxValueBytes)
        => FixedOverhead + maxKeyBytes + maxValueBytes;

    public static void Write(
        Span<byte> dest,
        long id,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        int maxKeyBytes,
        int maxValueBytes,
        long timestampMs)
    {
        dest.Clear(); // zero-pad unused bytes

        BinaryPrimitives.WriteInt64BigEndian(dest, id);
        BinaryPrimitives.WriteInt16BigEndian(dest[8..], (short)key.Length);
        key.CopyTo(dest[10..]);

        int vStart = 10 + maxKeyBytes;
        BinaryPrimitives.WriteInt16BigEndian(dest[vStart..], (short)value.Length);
        value.CopyTo(dest[(vStart + 2)..]);

        BinaryPrimitives.WriteInt64BigEndian(dest[(vStart + 2 + maxValueBytes)..], timestampMs);
    }

    public static ReadOnlySpan<byte> ReadValue(ReadOnlySpan<byte> src, int maxKeyBytes)
    {
        int vStart = 10 + maxKeyBytes;
        short vLen = BinaryPrimitives.ReadInt16BigEndian(src[vStart..]);
        if (vLen <= 0) return ReadOnlySpan<byte>.Empty;
        return src[(vStart + 2)..(vStart + 2 + vLen)];
    }
}
