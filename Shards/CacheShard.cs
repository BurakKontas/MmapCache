using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace MmapCacheApp.Shards;

/// <summary>
/// One memory-mapped shard file that stores <see cref="Capacity"/> fixed-size records.
/// Random access is O(1): position = offset × recordSize.
/// </summary>
internal sealed class CacheShard : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly int _recordSize;

    public int Id { get; }
    public long Capacity { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>Creates a new, blank shard file (build path).</summary>
    public CacheShard(int id, string path, int recordSize, long capacity)
        : this(id, path, recordSize, capacity, create: true) { }

    /// <summary>
    /// Opens an existing shard file written by a previous process run.
    /// Used during startup when resuming a still-fresh cache from disk.
    /// </summary>
    public static CacheShard OpenExisting(int id, string path, int recordSize, long capacity)
        => new(id, path, recordSize, capacity, create: false);

    private CacheShard(int id, string path, int recordSize, long capacity, bool create)
    {
        Id = id;
        Capacity = capacity;
        _recordSize = recordSize;

        long fileSize = recordSize * capacity;

        _mmf = MemoryMappedFile.CreateFromFile(
            path,
            create ? FileMode.Create : FileMode.Open,
            mapName: null,
            fileSize,
            MemoryMappedFileAccess.ReadWrite);

        _accessor = _mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.ReadWrite);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public void WriteRecord(long offset, byte[] record)
    {
        long pos = offset * _recordSize;
        _accessor.WriteArray(pos, record, 0, record.Length);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public byte[]? ReadValue(long offset, int maxKeyBytes)
    {
        long pos = offset * _recordSize;
        var buffer = new byte[_recordSize];
        _accessor.ReadArray(pos, buffer, 0, _recordSize);

        var span = Cache.CacheRecord.ReadValue(buffer, maxKeyBytes);
        return span.IsEmpty ? null : span.ToArray();
    }

    /// <summary>
    /// Reads the key stored at <paramref name="offset"/>.
    /// Returns <c>false</c> when the slot is empty (zero-padded), signalling
    /// that no further records exist in this shard beyond this point.
    /// Used during startup to re-index existing shard files without calling
    /// the data Supplier again.
    /// </summary>
    public bool TryReadKey(long offset, int maxKeyBytes, out string? key)
    {
        long pos = offset * _recordSize;
        var buffer = new byte[_recordSize];
        _accessor.ReadArray(pos, buffer, 0, _recordSize);

        // Record layout: [id 8B][kLen 2B][key maxKeyBytes B]…
        // An empty (zero-padded) slot has kLen == 0.
        short kLen = BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan()[8..]);
        if (kLen <= 0) { key = null; return false; }

        key = Encoding.UTF8.GetString(buffer, 10, kLen);
        return true;
    }

    // ── Misc ──────────────────────────────────────────────────────────────────

    public void Flush() => _accessor.Flush();

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
