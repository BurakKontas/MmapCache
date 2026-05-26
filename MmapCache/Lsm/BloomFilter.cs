using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MmapCache.Lsm;

/// <summary>
/// A zero-allocation, off-heap Counting Bloom Filter that utilizes unmanaged memory
/// to entirely bypass the Large Object Heap (LOH) and eliminate Garbage Collector pressure.
/// It employs lock striping via low-overhead unmanaged spin-locks to support concurrent operations.
/// </summary>
public unsafe class BloomFilter : IDisposable
{
    private byte* _counters;
    private int* _locks;

    private readonly int _hashFunctions;
    private readonly int _size;
    private long _elementCount;
    private int _isDisposed;

    // 4096 unmanaged spin-lock slots for fine-grained lock striping
    private const int LockCount = 4096;
    private const int LockMask = LockCount - 1;

    public BloomFilter(int capacity, double errorRate = 0.01)
    {
        // Calculate optimal bit array size: m = -(n * ln(p)) / (ln(2)^2)
        _size = (int)Math.Ceiling(capacity * Math.Log(errorRate) / Math.Log(1.0 / Math.Pow(2.0, Math.Log(2.0))));

        // Calculate optimal number of hash functions: k = (m / n) * ln(2)
        _hashFunctions = (int)Math.Round((_size / (double)capacity) * Math.Log(2.0));

        // Allocate entirely within unmanaged memory space to completely avoid LOH allocation
        _counters = (byte*)NativeMemory.AllocZeroed((nuint)_size);
        _locks = (int*)NativeMemory.AllocZeroed((nuint)(LockCount * sizeof(int)));
        _elementCount = 0;
    }

    public void Add(string key)
    {
        if (Volatile.Read(ref _isDisposed) == 1) return;

        int hash1 = GetHash1(key);
        int hash2 = GetHash2(key);

        for (int i = 0; i < _hashFunctions; i++)
        {
            int combinedHash = Math.Abs((hash1 + i * hash2) % _size);
            int lockIndex = combinedHash & LockMask;

            AcquireLock(lockIndex);
            try
            {
                // Cap at byte.MaxValue (255) to safely prevent counter overflow
                if (_counters[combinedHash] < byte.MaxValue)
                    _counters[combinedHash]++;
            }
            finally
            {
                ReleaseLock(lockIndex);
            }
        }
        Interlocked.Increment(ref _elementCount);
    }

    public bool Remove(string key)
    {
        if (Volatile.Read(ref _isDisposed) == 1 || !MightContain(key)) return false;

        int hash1 = GetHash1(key);
        int hash2 = GetHash2(key);

        for (int i = 0; i < _hashFunctions; i++)
        {
            int combinedHash = Math.Abs((hash1 + i * hash2) % _size);
            int lockIndex = combinedHash & LockMask;

            AcquireLock(lockIndex);
            try
            {
                if (_counters[combinedHash] > 0 && _counters[combinedHash] < byte.MaxValue)
                    _counters[combinedHash]--;
            }
            finally
            {
                ReleaseLock(lockIndex);
            }
        }
        Interlocked.Decrement(ref _elementCount);
        return true;
    }

    public bool MightContain(string key)
    {
        if (Volatile.Read(ref _isDisposed) == 1) return false;

        int hash1 = GetHash1(key);
        int hash2 = GetHash2(key);

        for (int i = 0; i < _hashFunctions; i++)
        {
            int combinedHash = Math.Abs((hash1 + i * hash2) % _size);

            // Volatile read is sufficient for bloom filter convergence checks without locking
            if (Volatile.Read(ref _counters[combinedHash]) == 0)
                return false; // Definitely not present
        }
        return true; // Might be present
    }

    public void Clear()
    {
        if (Volatile.Read(ref _isDisposed) == 1) return;
        NativeMemory.Clear(_counters, (nuint)_size);
        Interlocked.Exchange(ref _elementCount, 0);
    }

    public long Count => Interlocked.Read(ref _elementCount);

    private void AcquireLock(int index)
    {
        while (Interlocked.CompareExchange(ref _locks[index], 1, 0) != 0)
            Thread.SpinWait(1);
    }

    private void ReleaseLock(int index) => Volatile.Write(ref _locks[index], 0);

    private static int GetHash1(string key)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in key) hash = hash * 31 + c;
            return hash;
        }
    }

    private static int GetHash2(string key)
    {
        unchecked
        {
            int hash = 5381;
            foreach (char c in key) hash = ((hash << 5) + hash) + c;
            return hash;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            if (_counters != null) { NativeMemory.Free(_counters); _counters = null; }
            if (_locks != null) { NativeMemory.Free(_locks); _locks = null; }
        }
        GC.SuppressFinalize(this);
    }

    ~BloomFilter() => Dispose();
}