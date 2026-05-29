using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MmapCache.Lsm;

public delegate void ValueSpanConsumer(ReadOnlySpan<byte> valueSpan);

/// <summary>
/// A high-performance Log-Structured Merge (LSM) Tree engine adapted for high concurrency.
/// It uses a MemTable for in-memory active data, Write-Ahead Logs (WAL) for crash recovery,
/// and flushed unmanaged SSTable segments for durable disk storage.
/// </summary>
public unsafe class LsmEngine : IDisposable
{
    /// <summary>
    /// The currently active MemTable accepting new writes.
    /// </summary>
    private MemTable _activeMemTable;

    /// <summary>
    /// MemTables currently in the process of being flushed to disk. Keys in here remain readable.
    /// </summary>
    private readonly ConcurrentDictionary<int, MemTable> _flushingMemTables = new();

    /// <summary>
    /// Write-Ahead Log for durable persistence prior to flushing. 
    /// </summary>
    private WalWriter _activeWal;

    /// <summary>
    /// BloomFilter allowing 0-IO misses for non-existent keys.
    /// </summary>
    private readonly BloomFilter _bloomFilter;

    /// <summary>
    /// The global unmanaged radix index for all memory and disk segments.
    /// </summary>
    private readonly ConcurrentRadixTree<IndexRecord> _index;

    /// <summary>
    /// Tracks all active Memory-Mapped SSTable segments currently on disk.
    /// </summary>
    private readonly ConcurrentDictionary<int, Segment> _segments = new();

    private readonly object _flushLock = new();

    /// <summary>
    /// Backpressure semaphore limiting max concurrent background flush tasks.
    /// </summary>
    private readonly SemaphoreSlim _flushSemaphore = new(initialCount: 2, maxCount: 2);
    private readonly ReaderWriterLockSlim _engineLock = new(LockRecursionPolicy.NoRecursion);
    private readonly object _walLock = new();

    private readonly string _dir;
    private int _nextSegmentId = 0;
    private int _isDisposed = 0;
    private long _totalLiveCount = 0;
    private readonly long _flushThresholdBytes;
    private readonly int _radixTreeCapacity;

    public long Count => Interlocked.Read(ref _totalLiveCount);

    public LsmEngine(string directory, long flushThresholdBytes = 64 * 1024 * 1024, int radixTreeCapacity = 1_000_000, bool bootstrap = true)
    {
        _dir = directory;
        _flushThresholdBytes = flushThresholdBytes;
        _radixTreeCapacity = radixTreeCapacity;

        Directory.CreateDirectory(_dir);

        _index = new ConcurrentRadixTree<IndexRecord>(radixTreeCapacity);
        _bloomFilter = new BloomFilter(capacity: Math.Max(radixTreeCapacity, 1_000_000), errorRate: 0.01);
        _activeMemTable = new MemTable(_flushThresholdBytes, _radixTreeCapacity);

        if (bootstrap)
            Bootstrap();

        _activeWal = new WalWriter(Path.Combine(_dir, $"wal_{_nextSegmentId}.log"));
    }

    private void Bootstrap()
    {
        if (!Directory.Exists(_dir)) return;

        var sstFiles = Directory.GetFiles(_dir, "segment_*.sst")
            .Select(f => {
                var name = Path.GetFileNameWithoutExtension(f);
                int id = int.Parse(name.Split('_')[1]);
                return new { Id = id, Path = f };
            })
            .OrderBy(s => s.Id)
            .ToList();

        foreach (var sst in sstFiles)
        {
            var segment = new Segment(sst.Id, sst.Path);
            _segments[sst.Id] = segment;
            _nextSegmentId = Math.Max(_nextSegmentId, sst.Id + 1);

            using var fs = new FileStream(sst.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using var br = new BinaryReader(fs);

            long currentOffset = 0;
            long fileLength = fs.Length;

            while (currentOffset < fileLength)
            {
                bool isDeleted = br.ReadBoolean();
                int kLen = br.ReadInt32();
                int vLen = br.ReadInt32();
                byte[] keyBytes = br.ReadBytes(kLen);
                fs.Seek(vLen, SeekOrigin.Current);

                string key = Encoding.UTF8.GetString(keyBytes);
                long valOffset = currentOffset + 1 + 4 + 4 + kLen;

                if (!isDeleted)
                {
                    _index.Put(key, new IndexRecord
                    {
                        IsMemTable = false,
                        SegmentId = sst.Id,
                        Offset = valOffset,
                        Length = vLen
                    });
                    _bloomFilter.Add(key);
                    Interlocked.Increment(ref _totalLiveCount);
                }

                currentOffset += 1 + 4 + 4 + kLen + vLen;
            }
        }
    }

    public void Put(string key, ReadOnlySpan<byte> value)
    {
        _engineLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                throw new ObjectDisposedException(nameof(LsmEngine));

            ReadOnlySpan<byte> keyBytes = KeyEncoder.AsSpan(key);

            lock (_walLock)
            {
                _activeWal.Append(0, keyBytes, value);
            }

            _activeMemTable.Put(key, value);
            _bloomFilter.Add(key);
            Interlocked.Increment(ref _totalLiveCount);
        }
        finally
        {
            _engineLock.ExitReadLock();
        }

        if (_activeMemTable.Size >= _flushThresholdBytes)
            TriggerFlush();
    }

    public void Put(string key, byte[] value) => Put(key, value.AsSpan());

    public void Delete(string key)
    {
        _engineLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                throw new ObjectDisposedException(nameof(LsmEngine));

            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            lock (_walLock)
            {
                _activeWal.Append(1, keyBytes, ReadOnlySpan<byte>.Empty);
            }

            if (_index.TryGetValue(key, out _))
                Interlocked.Decrement(ref _totalLiveCount);

            _activeMemTable.Delete(key);
        }
        finally
        {
            _engineLock.ExitReadLock();
        }

        if (_activeMemTable.Size >= _flushThresholdBytes)
            TriggerFlush();
    }

    public bool TryGet(string key, ValueSpanConsumer consumer)
    {
        _engineLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1) return false;

            if (!_bloomFilter.MightContain(key))
                return false;

            if (_activeMemTable.TryGet(key, out bool isDeleted, out byte[] value))
            {
                if (isDeleted) return false;
                consumer(value);
                return true;
            }

            foreach (var segId in _flushingMemTables.Keys.OrderByDescending(x => x))
            {
                if (_flushingMemTables.TryGetValue(segId, out var flushingTable))
                {
                    if (flushingTable.TryGet(key, out isDeleted, out value))
                    {
                        if (isDeleted) return false;
                        consumer(value);
                        return true;
                    }
                }
            }

            if (_index.TryGetValue(key, out var indexRecord))
            {
                if (indexRecord.IsMemTable) return false;

                if (_segments.TryGetValue(indexRecord.SegmentId, out var segment))
                {
                    ReadOnlySpan<byte> viewSpan = segment.ReadValue(indexRecord.Offset, indexRecord.Length);
                    consumer(viewSpan);
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _engineLock.ExitReadLock();
        }
    }

    private void TriggerFlush()
    {
        if (!_flushSemaphore.Wait(0)) return;

        lock (_flushLock)
        {
            if (_activeMemTable.Size < _flushThresholdBytes)
            {
                _flushSemaphore.Release();
                return;
            }

            var oldMemTable = _activeMemTable;
            var oldWal = _activeWal;
            int currentSegmentId = _nextSegmentId++;

            _flushingMemTables[currentSegmentId] = oldMemTable;

            lock (_walLock)
            {
                _activeMemTable = new MemTable(_flushThresholdBytes, _radixTreeCapacity);
                _activeWal = new WalWriter(Path.Combine(_dir, $"wal_{_nextSegmentId}.log"));
            }

            Task.Run(() =>
            {
                try { FlushMemTableInternal(oldMemTable, currentSegmentId, oldWal); }
                finally
                {
                    _flushSemaphore.Release();
                }
            });
        }
    }

    private void FlushMemTableInternal(MemTable memTable, int newSegmentId, WalWriter oldWal)
    {
        string sstPath = Path.Combine(_dir, $"segment_{newSegmentId}.sst");
        long currentOffset = 0;

        var pendingIndexUpdates = new List<(string Key, IndexRecord Record)>();
        var snapshot = memTable.GetSnapshot();

        using (var fs = new FileStream(sstPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        using (var bw = new BinaryWriter(fs))
        {
            foreach (var kvp in snapshot)
            {
                if (kvp.Value.IsDeleted)
                {
                    _index.Remove(kvp.Key);
                    _bloomFilter.Remove(kvp.Key);
                    continue;
                }

                byte[] kBytes = Encoding.UTF8.GetBytes(kvp.Key);
                ReadOnlySpan<byte> vBytes = memTable.ReadRawArenaValue(kvp.Value.Offset, kvp.Value.Length);

                if (vBytes.IsEmpty) continue;

                bw.Write(false);
                bw.Write(kBytes.Length);
                bw.Write(vBytes.Length);
                bw.Write(kBytes);
                bw.Write(vBytes);

                long valOffset = currentOffset + 1 + 4 + 4 + kBytes.Length;

                pendingIndexUpdates.Add((kvp.Key, new IndexRecord
                {
                    IsMemTable = false,
                    SegmentId = newSegmentId,
                    Offset = valOffset,
                    Length = vBytes.Length
                }));

                currentOffset += 1 + 4 + 4 + kBytes.Length + vBytes.Length;
            }
        }

        _segments[newSegmentId] = new Segment(newSegmentId, sstPath);

        foreach (var update in pendingIndexUpdates)
        {
            _index.Put(update.Key, update.Record);
        }

        _engineLock.EnterWriteLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1) return;

            if (_flushingMemTables.TryRemove(newSegmentId, out var flushedTable))
                flushedTable.Dispose();

            oldWal?.Dispose();
            string oldWalPath = Path.Combine(_dir, $"wal_{newSegmentId}.log");
            if (File.Exists(oldWalPath))
                try { File.Delete(oldWalPath); } catch { }
        }
        finally
        {
            _engineLock.ExitWriteLock();
        }
    }

    public void ForceClearWalAndIndex()
    {
        _engineLock.EnterWriteLock();
        try
        {
            lock (_flushLock)
            {
                lock (_walLock)
                {
                    _activeWal?.Dispose();

                    foreach (var seg in _segments.Values) seg?.Dispose();
                    _activeMemTable?.Dispose();
                    foreach (var t in _flushingMemTables.Values) t?.Dispose();

                    _flushingMemTables.Clear();
                    _index.Clear();
                    _bloomFilter.Clear();
                    _segments.Clear();
                    Interlocked.Exchange(ref _totalLiveCount, 0);

                    if (Directory.Exists(_dir))
                    {
                        foreach (var file in Directory.GetFiles(_dir))
                            try { File.Delete(file); } catch { }
                    }

                    _nextSegmentId = 0;
                    _activeMemTable = new MemTable(_flushThresholdBytes, _radixTreeCapacity);
                    _activeWal = new WalWriter(Path.Combine(_dir, $"wal_{_nextSegmentId}.log"));
                }
            }
        }
        finally
        {
            _engineLock.ExitWriteLock();
        }
    }

    /// <summary>Returns all live keys from active MemTable, flushing MemTables, and segments.</summary>
    private IEnumerable<string> GetUnifiedKeys(string prefix)
    {
        var seen = new HashSet<string>();

        // 1. Active MemTable (most recent)
        foreach (var key in GetKeysFromMemTable(_activeMemTable, prefix, seen))
            yield return key;

        // 2. Flushing MemTables (ordered by segment id descending → newer first)
        var flushingCopy = _flushingMemTables.OrderByDescending(kvp => kvp.Key).ToList();
        foreach (var (_, mt) in flushingCopy)
        {
            foreach (var key in GetKeysFromMemTable(mt, prefix, seen))
                yield return key;
        }

        // 3. Segments (oldest)
        foreach (var key in _index.EnumerateKeysLazy(prefix))
        {
            if (seen.Add(key))
                yield return key;
        }
    }

    /// <summary>Enumerates live (non‑deleted) keys from a single MemTable, filtered by prefix.</summary>
    private IEnumerable<string> GetKeysFromMemTable(MemTable mt, string prefix, HashSet<string> seen)
    {
        foreach (var kvp in mt.GetSnapshot())
        {
            if (kvp.Value.IsDeleted) continue;
            if (!string.IsNullOrEmpty(prefix) && !kvp.Key.StartsWith(prefix))
                continue;
            if (seen.Add(kvp.Key))
                yield return kvp.Key;
        }
    }

    /// <summary>
    /// Enumerates all keys (with optional prefix filter) from the entire LSM tree.
    /// </summary>
    public IEnumerable<string> EnumerateKeys(string prefix = "", CancellationToken ct = default!)
    {
        _engineLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                yield break;
            foreach (var key in GetUnifiedKeys(prefix))
            {
                ct.ThrowIfCancellationRequested();
                yield return key;
            }
        }
        finally
        {
            _engineLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Zero‑allocation scan of keys (with optional prefix filter) from the entire LSM tree.
    /// </summary>
    public void ScanKeysZeroAlloc(RadixKeySpanConsumer consumer, string prefix = "", CancellationToken ct = default!)
    {
        _engineLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1)
                return;
            foreach (var key in GetUnifiedKeys(prefix))
            {
                ct.ThrowIfCancellationRequested();
                consumer(key.AsSpan());
            }
        }
        finally
        {
            _engineLock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _engineLock.EnterWriteLock();
            try
            {
                lock (_walLock)
                {
                    _activeWal?.Dispose();
                }

                if (_activeMemTable?.Size > 0)
                {
                    try { FlushMemTableInternal(_activeMemTable, _nextSegmentId++, _activeWal!); } catch { }
                }

                foreach (var t in _flushingMemTables.Values) t?.Dispose();
                _flushingMemTables.Clear();

                foreach (var seg in _segments.Values) seg?.Dispose();
                _segments.Clear();

                _index?.Dispose();
                _activeMemTable?.Dispose();
                _flushSemaphore?.Dispose();
                _bloomFilter?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Dispose error: {ex.Message}");
            }
            finally
            {
                _engineLock.ExitWriteLock();
                _engineLock.Dispose();
            }
        }
        GC.SuppressFinalize(this);
    }

    ~LsmEngine()
    {
        Dispose();
    }
}