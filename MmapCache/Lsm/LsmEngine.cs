using System.Collections.Concurrent;
using System.Text;

namespace MmapCache.Lsm;

public class LsmEngine : IDisposable
{
    private MemTable _activeMemTable = new();
    private readonly ConcurrentQueue<MemTable> _flushingMemTables = new();
    private WalWriter _activeWal;
    private readonly ConcurrentDictionary<string, IndexRecord> _index = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Segment> _segments = new();
    private readonly object _flushLock = new();

    private readonly string _dir;
    private int _nextSegmentId = 0;
    
    // Configs
    private readonly long _flushThresholdBytes = 64 * 1024 * 1024; // 64 MB

    public LsmEngine(string directory, long flushThresholdBytes = 64 * 1024 * 1024)
    {
        _dir = directory;
        _flushThresholdBytes = flushThresholdBytes;
        
        Directory.CreateDirectory(_dir);
        
        Bootstrap();

        // Setup initial WAL
        string walPath = Path.Combine(_dir, $"wal_{_nextSegmentId}.log");
        _activeWal = new WalWriter(walPath);
    }

    private void Bootstrap()
    {
        int maxId = 0;
        var sstFiles = Directory.GetFiles(_dir, "segment_*.sst");
        foreach (var sst in sstFiles)
        {
            string name = Path.GetFileNameWithoutExtension(sst);
            if (!int.TryParse(name.Substring("segment_".Length), out int sid)) continue;
            maxId = Math.Max(maxId, sid);

            var segment = new Segment(sid, sst);
            _segments[sid] = segment;
            
            using var fs = new FileStream(sst, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            long offset = 0;
            while (fs.Position < fs.Length)
            {
                long recStart = offset;
                bool isDeleted = br.ReadBoolean();
                int kLen = br.ReadInt32();
                int vLen = br.ReadInt32();
                byte[] kBytes = br.ReadBytes(kLen);
                string key = Encoding.UTF8.GetString(kBytes);
                
                if (!isDeleted)
                {
                    fs.Position += vLen; // skip value
                    _index[key] = new IndexRecord { IsMemTable = false, SegmentId = sid, Offset = recStart + 9 + kLen, Length = vLen };
                }
                else
                {
                    _index.TryRemove(key, out _);
                }
                offset += 9 + kLen + (isDeleted ? 0 : vLen);
            }
        }

        var walFiles = Directory.GetFiles(_dir, "wal_*.log");
        foreach (var wal in walFiles)
        {
            string name = Path.GetFileNameWithoutExtension(wal);
            if (!int.TryParse(name.Substring("wal_".Length), out int wid)) continue;
            maxId = Math.Max(maxId, wid);

            using var fs = new FileStream(wal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            while (fs.Position < fs.Length)
            {
                if (fs.Length - fs.Position < 4) break;
                uint crc = br.ReadUInt32();
                
                try
                {
                    byte op = br.ReadByte();
                    int kLen = br.ReadInt32();
                    int vLen = br.ReadInt32();
                    byte[] kBytes = br.ReadBytes(kLen);
                    byte[] vBytes = br.ReadBytes(vLen);

                    string key = Encoding.UTF8.GetString(kBytes);
                    if (op == 0)
                    {
                        _activeMemTable.Put(key, vBytes);
                        _index[key] = new IndexRecord { IsMemTable = true };
                    }
                    else
                    {
                        _activeMemTable.Delete(key);
                        _index[key] = new IndexRecord { IsMemTable = true };
                    }
                }
                catch (EndOfStreamException) { break; }
            }
        }
        
        _nextSegmentId = maxId + 1;
    }

    public void Put(string key, byte[] value)
    {
        var keySpan = Encoding.UTF8.GetBytes(key);
        
        lock (_flushLock)
        {
            _activeWal.Append(0, keySpan, value);
            _activeMemTable.Put(key, value);
            _index[key] = new IndexRecord { IsMemTable = true };
        }

        CheckFlush();
    }

    public ReadOnlySpan<byte> Get(string key)
    {
        if (!_index.TryGetValue(key, out var record))
        {
            return default;
        }

        if (record.IsMemTable)
        {
            if (_activeMemTable.TryGet(key, out bool isDeleted, out byte[] value))
            {
                if (isDeleted) return default;
                return value;
            }
            
            foreach (var flushing in _flushingMemTables)
            {
                if (flushing.TryGet(key, out isDeleted, out value))
                {
                    if (isDeleted) return default;
                    return value;
                }
            }
        }

        if (_segments.TryGetValue(record.SegmentId, out var segment))
        {
            return segment.ReadValue(record.Offset, record.Length);
        }

        return default;
    }

    public void Delete(string key)
    {
        var keySpan = Encoding.UTF8.GetBytes(key);
        
        lock (_flushLock)
        {
            _activeWal.Append(1, keySpan, ReadOnlySpan<byte>.Empty);
            _activeMemTable.Delete(key);
            // Tomstone in index so it masks disk values
            _index[key] = new IndexRecord { IsMemTable = true };
        }
        
        CheckFlush();
    }
    
    public long Count => _index.Count;

    private void CheckFlush()
    {
        if (_activeMemTable.Size >= _flushThresholdBytes)
        {
            ThreadPool.QueueUserWorkItem(_ => FlushActiveMemTable());
        }
    }

    private void FlushActiveMemTable()
    {
        MemTable toFlush;
        WalWriter oldWal;
        int newSegmentId;
        string newWalPath;
        
        lock (_flushLock)
        {
            if (_activeMemTable.Size < _flushThresholdBytes) return;

            toFlush = _activeMemTable;
            oldWal = _activeWal;
            
            _nextSegmentId++;
            newSegmentId = _nextSegmentId;
            newWalPath = Path.Combine(_dir, $"wal_{newSegmentId}.log");
            
            // Swap
            _flushingMemTables.Enqueue(toFlush);
            _activeMemTable = new MemTable();
            _activeWal = new WalWriter(newWalPath);
        }

        string sstPath = Path.Combine(_dir, $"segment_{newSegmentId}.sst");
        long currentOffset = 0;
        
        // Write SSTable (basic layout: sequences of KeyLen, ValLen, Key, Val)
        using (var fs = new FileStream(sstPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            var snapshot = toFlush.GetSnapshot().ToList();
            
            // In a real LSM, we'd sort keys. For now, we write and create an index footer.
            foreach (var kvp in snapshot)
            {
                var kBytes = Encoding.UTF8.GetBytes(kvp.Key);
                var vBytes = kvp.Value.Value;
                bool isDeleted = kvp.Value.IsDeleted;
                
                // Keep record of offset for the global index
                long recordOffset = currentOffset; 
                int recordLength = vBytes.Length; // For value retrieval
                
                bw.Write(isDeleted);
                bw.Write(kBytes.Length);
                bw.Write(vBytes.Length);
                bw.Write(kBytes);
                if (!isDeleted)
                {
                    bw.Write(vBytes);
                }

                if (!isDeleted)
                {
                    // Calculate value offset (1 + 4 + 4 + kLen)
                    long valOffset = recordOffset + 9 + kBytes.Length;
                    var newDiskRecord = new IndexRecord { IsMemTable = false, SegmentId = newSegmentId, Offset = valOffset, Length = recordLength };
                    
                    // Update global index if it hasn't been overwritten in memory
                    _index.AddOrUpdate(kvp.Key, 
                        key => newDiskRecord,
                        (key, existing) => 
                        {
                            if (existing.IsMemTable && _activeMemTable.TryGet(key, out _, out _))
                            {
                                return existing;
                            }
                            return newDiskRecord;
                        });
                }
                else
                {
                    // Deleted, try remove from index
                    if (!_activeMemTable.TryGet(kvp.Key, out _, out _))
                    {
                        var kvpToRemove = new System.Collections.Generic.KeyValuePair<string, IndexRecord>(kvp.Key, new IndexRecord { IsMemTable = true });
                        ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, IndexRecord>>)_index).Remove(kvpToRemove);
                    }
                }

                currentOffset += 9 + kBytes.Length + (isDeleted ? 0 : vBytes.Length);
            }
            
            // Note: A real SSTable has an index block footer to bootstrap the dictionary at restart.
        }

        var segment = new Segment(newSegmentId, sstPath);
        _segments[newSegmentId] = segment;
        
        // Remove from flushing queue (we only ever have 1 or a few, and they complete in order roughly. For exact removal we can copy to list or ignore for this toy engine)
        // Since we enqueue and dequeue sequentially in flush blocks
        _flushingMemTables.TryDequeue(out _);

        // Cleanup old WAL
        oldWal.Dispose();
        // In real impl, delete the WAL file.
    }
    
    public void Clear()
    {
        lock (_flushLock)
        {
            // Aktif WAL ve Segmentleri kapat
            _activeWal.Dispose();
            foreach (var seg in _segments.Values)
            {
                seg.Dispose();
            }

            // In-Memory state'i sıfırla
            _activeMemTable = new MemTable();
            while (_flushingMemTables.TryDequeue(out _)) { }
            _index.Clear();
            _segments.Clear();

            // Diskteki eski WAL ve SSTable dosyalarını sil
            foreach (var file in Directory.GetFiles(_dir))
            {
                try 
                { 
                    File.Delete(file); 
                } 
                catch 
                { 
                    // İşletim sistemi kaynaklı anlık lock'ları yoksay
                }
            }

            // Motoru ilk haline getir
            _nextSegmentId = 0;
            string walPath = Path.Combine(_dir, $"wal_{_nextSegmentId}.log");
            _activeWal = new WalWriter(walPath);
        }
    }

    public void Dispose()
    {
        _activeWal.Dispose();
        foreach (var seg in _segments.Values)
        {
            seg.Dispose();
        }
    }
}
