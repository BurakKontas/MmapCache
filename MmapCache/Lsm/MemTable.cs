using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MmapCache.Lsm;

/// <summary>
/// A high-performance, zero-allocation active in-memory table.
/// It uses an unmanaged RadixTree and a contiguous memory arena for avoiding Garbage Collection overhead.
/// Data is appended linearly during active writes.
/// </summary>
public unsafe class MemTable : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MemTableRecord
    {
        public long Offset;
        public int Length;
        public bool IsDeleted;
    }

    /// <summary>
    /// The unmanaged radix tree holding key indexing and metadata.
    /// </summary>
    private readonly ConcurrentRadixTree<MemTableRecord> _tree;
    private byte* _arenaBuffer;
    private long _arenaCapacity;
    private long _arenaOffset;
    private long _estimatedSize;
    private readonly object _arenaLock = new();
    private int _isDisposed;

    /// <summary>
    /// A hard limit on the dynamic expansion of the unmanaged arena size.
    /// This prevents catastrophic memory growth when asynchronous flushes lag behind heavy load.
    /// Default is generally 4 times the configured start capacity limit.
    /// </summary>
    private readonly long _arenaHardCap;

    private readonly ReaderWriterLockSlim _disposeLock = new();

    public long Size => Volatile.Read(ref _estimatedSize);

    public MemTable(long initialArenaCapacityBytes = 32 * 1024 * 1024, int initialTreeCapacity = 200_000)
    {
        _arenaCapacity = initialArenaCapacityBytes;
        _arenaHardCap = initialArenaCapacityBytes * 4;
        _arenaBuffer = (byte*)NativeMemory.Alloc((nuint)_arenaCapacity);
        _tree = new ConcurrentRadixTree<MemTableRecord>(initialTreeCapacity);
    }

    public void Put(string key, ReadOnlySpan<byte> value)
    {
        _disposeLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1) return;

            long offset;
            lock (_arenaLock)
            {
                EnsureCapacity(value.Length);
                offset = _arenaOffset;
                fixed (byte* src = value)
                {
                    Buffer.MemoryCopy(src, _arenaBuffer + offset, value.Length, value.Length);
                }
                _arenaOffset += value.Length;
            }

            var record = new MemTableRecord { Offset = offset, Length = value.Length, IsDeleted = false };
            _tree.Put(key, record);
            Interlocked.Add(ref _estimatedSize, key.Length * 2 + value.Length + sizeof(MemTableRecord));
        }
        finally
        {
            _disposeLock.ExitReadLock();
        }
    }

    public void Delete(string key)
    {
        _disposeLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1) return;

            var record = new MemTableRecord { Offset = 0, Length = 0, IsDeleted = true };
            _tree.Put(key, record);
            Interlocked.Add(ref _estimatedSize, key.Length * 2 + sizeof(MemTableRecord));
        }
        finally
        {
            _disposeLock.ExitReadLock();
        }
    }

    public bool TryGet(string key, out bool isDeleted, out byte[] value)
    {
        value = null;
        isDeleted = false;

        _disposeLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1 || _arenaBuffer == null)
                return false;

            if (!_tree.TryGetValue(key, out var record))
                return false;

            isDeleted = record.IsDeleted;
            if (isDeleted) return true;

            value = new byte[record.Length];
            fixed (byte* dst = value)
            {
                Buffer.MemoryCopy(_arenaBuffer + record.Offset, dst, record.Length, record.Length);
            }
            return true;
        }
        finally
        {
            _disposeLock.ExitReadLock();
        }
    }

    public ReadOnlySpan<byte> ReadRawArenaValue(long offset, int length)
    {
        _disposeLock.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1 || _arenaBuffer == null)
                return ReadOnlySpan<byte>.Empty;

            return new ReadOnlySpan<byte>(_arenaBuffer + offset, length);
        }
        finally
        {
            _disposeLock.ExitReadLock();
        }
    }

    public System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, MemTableRecord>> GetSnapshot()
    {
        _disposeLock.EnterReadLock();
        try
        {
            return Volatile.Read(ref _isDisposed) == 1 ? [] : _tree.ToKeyValuePairList();
        }
        finally
        {
            _disposeLock.ExitReadLock();
        }
    }

    private void EnsureCapacity(int requiredBytes)
    {
        if (_arenaOffset + requiredBytes <= _arenaCapacity) return;

        long newCapacity = _arenaCapacity * 2;
        while (_arenaOffset + requiredBytes > newCapacity)
            newCapacity *= 2;

        newCapacity = Math.Min(newCapacity, _arenaHardCap);

        byte* newArena = (byte*)NativeMemory.Realloc(_arenaBuffer, (nuint)newCapacity);
        _arenaBuffer = newArena;
        _arenaCapacity = newCapacity;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            if (disposing)
            {
                _disposeLock.EnterWriteLock();
                try
                {
                    if (_arenaBuffer != null)
                    {
                        NativeMemory.Free(_arenaBuffer);
                        _arenaBuffer = null;
                    }
                    _tree.Dispose();
                }
                finally
                {
                    _disposeLock.ExitWriteLock();
                }
                _disposeLock.Dispose();
            }
            else
            {
                if (_arenaBuffer != null)
                {
                    NativeMemory.Free(_arenaBuffer);
                    _arenaBuffer = null;
                }
            }
        }
    }

    ~MemTable()
    {
        Dispose(false);
    }
}