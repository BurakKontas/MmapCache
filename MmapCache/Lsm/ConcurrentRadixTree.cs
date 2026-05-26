using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MmapCache.Lsm;

/// <summary>
/// A highly optimized, zero-allocation, off-heap Radix Tree implementation using the 
/// Left-Child Right-Sibling (LCRS) representation. This replaces fixed 256-pointer arrays 
/// with a linked-list child pattern, shrinking node memory from 1042 bytes to 27 bytes.
/// </summary>
public unsafe class ConcurrentRadixTree<T> : IDisposable where T : unmanaged
{
    // Node layout packed to 1-byte boundaries to minimize cache line footprint.
    // Size: sizeof(T) + 4 (Child) + 4 (Sibling) + 1 (KeyChar) + 1 (HasValue) = 27 bytes (when T is IndexRecord)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Node
    {
        public T Value;
        public int Child;    // Index of the first child node (down a level)
        public int Sibling;  // Index of the next sibling node (same level)
        public byte KeyChar; // Edge character representation
        public bool HasValue;// Flag indicating if this node contains a terminal value
    }

    private Node** _segments;
    private int _segmentSize;
    private int _segmentCount;
    private int _capacity;
    private int _next;
    private long _count;

    private readonly ReaderWriterLockSlim _treeLock = new(LockRecursionPolicy.NoRecursion);
    private readonly object _segLock = new();
    private int _isDisposed;

    public long Count => Interlocked.Read(ref _count);

    public ConcurrentRadixTree(int capacity = 65_000_000, int segmentSize = 131_072)
    {
        _segmentSize = segmentSize;
        _capacity = capacity;

        int initialPtrs = Math.Max(8, capacity / segmentSize + 2);
        _segments = (Node**)NativeMemory.AllocZeroed((nuint)(initialPtrs * sizeof(Node*)));
        _segmentCount = initialPtrs;

        EnsureSegment(0);
        _next = 1; // Index 0 serves as the dummy root node
    }

    private ref Node GetNode(int index)
    {
        int seg = index / _segmentSize;
        int off = index % _segmentSize;
        return ref _segments[seg][off];
    }

    private void EnsureSegment(int seg)
    {
        if (seg < Volatile.Read(ref _segmentCount) && _segments[seg] != null) return;
        lock (_segLock)
        {
            if (seg >= _segmentCount)
            {
                int newSize = _segmentCount;
                while (newSize <= seg) newSize = Math.Min(newSize * 2, newSize + 1024);
                var newPtrs = (Node**)NativeMemory.AllocZeroed((nuint)(newSize * sizeof(Node*)));
                Buffer.MemoryCopy(_segments, newPtrs, newSize * sizeof(Node*), _segmentCount * sizeof(Node*));
                NativeMemory.Free(_segments);
                _segments = newPtrs;
                Volatile.Write(ref _segmentCount, newSize);
            }
            if (_segments[seg] == null)
            {
                _segments[seg] = (Node*)NativeMemory.AllocZeroed((nuint)_segmentSize, (nuint)sizeof(Node));
            }
        }
    }

    private void PreEnsureForKey(string key)
    {
        int worstNext = _next + key.Length;
        int worstSeg = worstNext / _segmentSize;
        for (int s = 0; s <= worstSeg; s++) EnsureSegment(s);
    }

    public void Put(string key, T value)
    {
        if (Volatile.Read(ref _isDisposed) == 1) return;
        PreEnsureForKey(key);

        _treeLock.EnterWriteLock();
        try
        {
            int index = 0; // Start at the root node
            for (int i = 0; i < key.Length; i++)
            {
                byte c = (byte)key[i];
                ref Node node = ref GetNode(index);

                if (node.Child == 0)
                {
                    // If no children exist, create the first child node directly
                    if (_next >= _capacity) throw new OutOfMemoryException($"Trie capacity exhausted: {_capacity}");
                    int newIdx = _next++;
                    node.Child = newIdx;
                    index = newIdx;
                    ref Node newChild = ref GetNode(index);
                    newChild.KeyChar = c;
                }
                else
                {
                    // Search horizontally across siblings for a matching edge character
                    int currChild = node.Child;
                    int prevChild = 0;
                    bool found = false;

                    while (currChild != 0)
                    {
                        ref Node childNode = ref GetNode(currChild);
                        if (childNode.KeyChar == c)
                        {
                            index = currChild;
                            found = true;
                            break;
                        }
                        prevChild = currChild;
                        currChild = childNode.Sibling;
                    }

                    if (!found)
                    {
                        // Character not found among siblings; append it to the end of the sibling chain
                        if (_next >= _capacity) throw new OutOfMemoryException($"Trie capacity exhausted: {_capacity}");
                        int newNodeIdx = _next++;
                        ref Node prevNode = ref GetNode(prevChild);
                        prevNode.Sibling = newNodeIdx;
                        index = newNodeIdx;

                        ref Node newChildNode = ref GetNode(index);
                        newChildNode.KeyChar = c;
                    }
                }
            }

            ref Node final = ref GetNode(index);
            if (!final.HasValue) Interlocked.Increment(ref _count);
            final.Value = value;
            final.HasValue = true;
        }
        finally
        {
            _treeLock.ExitWriteLock();
        }
    }

    public bool TryGetValue(string key, out T value)
    {
        value = default;
        if (Volatile.Read(ref _isDisposed) == 1) return false;

        _treeLock.EnterReadLock();
        try
        {
            int index = 0;
            for (int i = 0; i < key.Length; i++)
            {
                byte c = (byte)key[i];
                ref Node node = ref GetNode(index);

                int currChild = node.Child;
                bool found = false;

                while (currChild != 0)
                {
                    ref Node childNode = ref GetNode(currChild);
                    if (childNode.KeyChar == c)
                    {
                        index = currChild;
                        found = true;
                        break;
                    }
                    currChild = childNode.Sibling;
                }

                if (!found) return false;
            }

            ref Node final = ref GetNode(index);
            if (!final.HasValue) return false;
            value = final.Value;
            return true;
        }
        finally
        {
            _treeLock.ExitReadLock();
        }
    }

    public bool Remove(string key)
    {
        if (Volatile.Read(ref _isDisposed) == 1) return false;

        _treeLock.EnterWriteLock();
        try
        {
            int index = 0;
            for (int i = 0; i < key.Length; i++)
            {
                byte c = (byte)key[i];
                ref Node node = ref GetNode(index);

                int currChild = node.Child;
                bool found = false;

                while (currChild != 0)
                {
                    ref Node childNode = ref GetNode(currChild);
                    if (childNode.KeyChar == c)
                    {
                        index = currChild;
                        found = true;
                        break;
                    }
                    currChild = childNode.Sibling;
                }

                if (!found) return false;
            }

            ref Node final = ref GetNode(index);
            if (!final.HasValue) return false;
            final.HasValue = false;
            final.Value = default;
            Interlocked.Decrement(ref _count);
            return true;
        }
        finally
        {
            _treeLock.ExitWriteLock();
        }
    }

    public List<KeyValuePair<string, T>> GetSnapshot()
    {
        return ToKeyValuePairList();
    }

    /// <summary>
    /// 🔥 FIX: LsmEngine.cs tarafındaki memTable.GetSnapshot() kilit mekanizmasının 
    /// beklediği güncel ve uyumlu metot ismi.
    /// </summary>
    public List<KeyValuePair<string, T>> ToKeyValuePairList()
    {
        if (Volatile.Read(ref _isDisposed) == 1) return new List<KeyValuePair<string, T>>();

        var result = new List<KeyValuePair<string, T>>((int)Math.Max(1, Count));
        _treeLock.EnterReadLock();
        try
        {
            // String birleştirmelerini (GC Allocation) önlemek için max 512 karakterlik kiralık buffer alanı
            char* buffer = stackalloc char[512];
            TraverseOptimized(0, buffer, 0, result);
        }
        finally
        {
            _treeLock.ExitReadLock();
        }
        return result;
    }

    /// <summary>
    /// 🔥 OPTIMIZATION: Önceki sürümdeki `prefix + char` mantığı her adımda yeni string oluşturup
    /// Garbage Collector (GC) üzerinde devasa yük bindiriyordu. Bu pointer tabanlı traverse sürümü 
    /// sıfır allocation (Zero-Allocation) ile çalışır.
    /// </summary>
    private void TraverseOptimized(int index, char* buffer, int depth, List<KeyValuePair<string, T>> result)
    {
        ref Node node = ref GetNode(index);

        if (index != 0 && node.HasValue)
        {
            // Sadece terminal (değer içeren) düğümlere gelindiğinde tek bir string oluşturulur
            string key = new string(buffer, 0, depth);
            result.Add(new KeyValuePair<string, T>(key, node.Value));
        }

        int currChild = node.Child;
        while (currChild != 0)
        {
            ref Node childNode = ref GetNode(currChild);

            // Karakteri derinliğe göre buffer'a güvenle yazıp bir alt kırılıma iniyoruz
            if (depth < 512)
            {
                buffer[depth] = (char)childNode.KeyChar;
                TraverseOptimized(currChild, buffer, depth + 1, result);
            }

            currChild = childNode.Sibling;
        }
    }

    public void Clear()
    {
        if (Volatile.Read(ref _isDisposed) == 1) return;
        _treeLock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _segmentCount; i++)
                if (_segments[i] != null)
                    NativeMemory.Clear(_segments[i], (nuint)(_segmentSize * sizeof(Node)));
            _next = 1;
            _count = 0;
        }
        finally { _treeLock.ExitWriteLock(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return;
        _treeLock.EnterWriteLock();
        try
        {
            if (_segments != null)
            {
                for (int i = 0; i < _segmentCount; i++)
                    if (_segments[i] != null) NativeMemory.Free(_segments[i]);
                NativeMemory.Free(_segments);
                _segments = null;
            }
        }
        finally { _treeLock.ExitWriteLock(); }
        _treeLock.Dispose();
    }

    ~ConcurrentRadixTree() => Dispose();
}