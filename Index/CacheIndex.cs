using System.Buffers.Binary;
using System.Text;
using FASTER.core;
using MmapCacheApp.Cache;

namespace MmapCacheApp.Index;

/// <summary>
/// Off-heap sharded index: string key → CacheLocation.
///
/// Each shard is a FasterKV&lt;SpanByte, SpanByte&gt; instance backed by a log file on disk.
/// All index data lives outside the GC heap — identical to ChronicleMap in the Java library.
///
/// Sharding:  indexShardId = (key.GetHashCode() &amp; 0x7FFF_FFFF) % shardCount
/// </summary>
internal sealed class CacheIndex : IDisposable
{
    private readonly FasterKV<SpanByte, SpanByte>[] _shards;
    private readonly IDevice[] _devices;
    private readonly int _shardCount;

    // CacheLocation serialized as 12 bytes: 4 (int shardId) + 8 (long offset)
    private const int LocBytes = 12;

    public CacheIndex(string baseDir, int shardCount = 16)
    {
        _shardCount = shardCount;
        _shards = new FasterKV<SpanByte, SpanByte>[shardCount];
        _devices = new IDevice[shardCount];

        Directory.CreateDirectory(baseDir);

        for (int i = 0; i < shardCount; i++)
        {
            string path = Path.Combine(baseDir, $"index_{i:D4}.log");

            _devices[i] = Devices.CreateLogDevice(path, preallocateFile: false, deleteOnClose: false);

            // FasterKVSettings uses byte sizes (not bit-shift properties).
            // PageSize / MemorySize / SegmentSize must be powers of 2.
            var settings = new FasterKVSettings<SpanByte, SpanByte>
            {
                LogDevice = _devices[i],
                PageSize = 4 * 1024,        //   4 KB per page
                MemorySize = 256 * 1024,         // 256 KB hot in-memory tier; rest spills to disk
                SegmentSize = 4 * 1024 * 1024, //   4 MB per segment file
                IndexSize = 16 * 1024,         //  16 KB hash index (= 256 buckets of 64 bytes)
            };

            _shards[i] = new FasterKV<SpanByte, SpanByte>(settings);
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public unsafe void Set(string key, CacheLocation location)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        Span<byte> valBuf = stackalloc byte[LocBytes];
        WriteLocation(valBuf, location);

        fixed (byte* kPtr = keyBytes)
        fixed (byte* vPtr = valBuf)
        {
            var kSpan = SpanByte.FromPointer(kPtr, keyBytes.Length);
            var vSpan = SpanByte.FromPointer(vPtr, LocBytes);

            using var session = _shards[ShardId(key)]
                .NewSession(new SpanByteFunctions<Empty>());
            session.Upsert(ref kSpan, ref vSpan);
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public unsafe bool TryGet(string key, out CacheLocation location)
    {
        location = default;
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);

        fixed (byte* kPtr = keyBytes)
        {
            var kSpan = SpanByte.FromPointer(kPtr, keyBytes.Length);
            var shard = _shards[ShardId(key)];
            using var session = shard.NewSession(new SpanByteFunctions<Empty>());

            SpanByteAndMemory output = default;
            var status = session.Read(ref kSpan, ref output);

            if (status.IsPending)
            {
                // CompletePendingWithOutputs correctly returns the disk-read result.
                // The old pattern (CompletePending + output=default + second Read)
                // discarded the output and could leave output in a bad state.
                session.CompletePendingWithOutputs(out var completedOutputs, wait: true);
                using (completedOutputs)
                {
                    if (!completedOutputs.Next()) return false;
                    status = completedOutputs.Current.Status;
                    output = completedOutputs.Current.Output;
                }
            }

            if (!status.Found) return false;

            // ⚠️  CRITICAL: read the SpanByte HERE, inside the fixed block and
            // while the session is still alive.  SpanByte holds a raw pointer
            // into FasterKV's log buffer; once the session is disposed that
            // memory is no longer protected and becomes a dangling reference.
            ReadOnlySpan<byte> valSpan = output.IsSpanByte
                ? output.SpanByte.AsReadOnlySpan()
                : output.Memory.Memory.Span;

            location = ReadLocation(valSpan);

            // Release the heap allocation (no-op for the SpanByte path).
            if (!output.IsSpanByte) output.Memory?.Dispose();

            return true;
        }
    }

    public bool Contains(string key) => TryGet(key, out _);

    public long Count => _shards.Sum(s => (long)s.EntryCount);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int ShardId(string key) => (key.GetHashCode() & 0x7FFF_FFFF) % _shardCount;

    private static void WriteLocation(Span<byte> buf, CacheLocation loc)
    {
        BinaryPrimitives.WriteInt32BigEndian(buf, loc.ShardId);
        BinaryPrimitives.WriteInt64BigEndian(buf[4..], loc.Offset);
    }

    private static CacheLocation ReadLocation(ReadOnlySpan<byte> buf)
        => new(BinaryPrimitives.ReadInt32BigEndian(buf),
               BinaryPrimitives.ReadInt64BigEndian(buf[4..]));

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var s in _shards) s.Dispose();
        foreach (var d in _devices) d.Dispose();
    }
}