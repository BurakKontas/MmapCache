using System.Collections.Concurrent;

namespace MmapCache.Lsm;

public class MemTable
{
    // Key -> (IsDeleted, Value)
    private readonly ConcurrentDictionary<string, (bool IsDeleted, byte[] Value)> _table = new(StringComparer.Ordinal);
    private long _estimatedSize = 0;

    public void Put(string key, byte[] value)
    {
        _table[key] = (false, value);
        Interlocked.Add(ref _estimatedSize, key.Length + value.Length);
    }

    public void Delete(string key)
    {
        _table[key] = (true, Array.Empty<byte>());
        Interlocked.Add(ref _estimatedSize, key.Length);
    }

    public ReadOnlySpan<byte> Get(string key)
    {
        if (_table.TryGetValue(key, out var record))
        {
            if (record.IsDeleted) return default;
            return record.Value;
        }
        return default;
    }

    public bool TryGet(string key, out bool isDeleted, out byte[] value)
    {
        if (_table.TryGetValue(key, out var record))
        {
            isDeleted = record.IsDeleted;
            value = record.Value;
            return true;
        }
        isDeleted = false;
        value = Array.Empty<byte>();
        return false;
    }

    public long Size => Interlocked.Read(ref _estimatedSize);
    
    public IEnumerable<KeyValuePair<string, (bool IsDeleted, byte[] Value)>> GetSnapshot() => _table;
}

