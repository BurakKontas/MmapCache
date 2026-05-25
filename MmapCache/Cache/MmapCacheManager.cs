using System.Collections.Concurrent;
using MmapCache.Config;
using MmapCache.Lsm;

namespace MmapCache.Cache;

public sealed class MmapCacheManager : IAsyncDisposable
{
    private static MmapCacheManager? _instance;
    
    public static MmapCacheManager Initialize(string basePath, int shardCapacity = 200_000, int memoryCacheSize = 10_000, int indexShardCount = 16, TimeSpan? ttlCheck = null)
    {
        if (_instance is not null) throw new InvalidOperationException("Already initialized.");
        _instance = new MmapCacheManager(basePath);
        return _instance;
    }
    
    public static MmapCacheManager Instance => _instance ?? throw new InvalidOperationException("Call Initialize() first.");
    
    private readonly string _basePath;
    private readonly ConcurrentDictionary<string, ICacheDefinition> _defs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LsmEngine> _engines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastReloads = new(StringComparer.Ordinal);

    private MmapCacheManager(string basePath)
    {
        _basePath = basePath;
        Directory.CreateDirectory(basePath);
    }

    public void Register<TValue>(MmapCacheDefinition<TValue> def)
    {
        _defs[def.Name] = def;
        string engineDir = Path.Combine(_basePath, def.Name);
        var engine = new LsmEngine(engineDir);
        _engines[def.Name] = engine;
        
        if (engine.Count == 0) 
        {
            LoadFromSupplier(def, engine);
            _lastReloads[def.Name] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// TTL verilirse o süre sonunda veri silinir. Verilmezse definition'daki TTL kullanılır.
    /// TimeSpan.Zero verilirse veri sonsuza kadar kalır.
    /// </summary>
    public void Put<TValue>(string cache, string key, TValue value, TimeSpan? ttl = null)
    {
        if (!_engines.TryGetValue(cache, out var engine)) return;
        var def = GetDef<TValue>(cache);
        byte[] valBytes = def.Serializer(value);
        
        TimeSpan finalTtl = ttl ?? def.Ttl;
        long expireTicks = finalTtl > TimeSpan.Zero ? (DateTime.UtcNow + finalTtl).Ticks : 0;

        // Payload: [ExpireTicks: 8 bytes] + [ValueBytes...]
        byte[] payload = new byte[8 + valBytes.Length];
        BitConverter.TryWriteBytes(payload, expireTicks);
        valBytes.CopyTo(payload.AsSpan(8));
        
        engine.Put(key, payload);
    }

    public void Delete(string cache, string key)
    {
        if (_engines.TryGetValue(cache, out var engine))
        {
            engine.Delete(key);
        }
    }

    public TValue? Get<TValue>(string cache, string key)
    {
        TryGet<TValue>(cache, key, out var v);
        return v;
    }

    public bool TryGet<TValue>(string cache, string key, out TValue? value)
    {
        value = default;
        if (!_engines.TryGetValue(cache, out var engine)) return false;
        
        var span = engine.Get(key);
        if (span.IsEmpty) return false;

        // TTL Kontrolü
        long expireTicks = BitConverter.ToInt64(span.Slice(0, 8));
        if (expireTicks > 0 && DateTime.UtcNow.Ticks > expireTicks)
        {
            // Süresi dolmuşsa "Lazy Expiration" ile sil ve bulamadım dön
            engine.Delete(key);
            return false;
        }

        var def = GetDef<TValue>(cache);
        value = def.Deserializer(span.Slice(8).ToArray());
        return true;
    }

    public bool Exists(string cache, string key)
    {
        if (!_engines.TryGetValue(cache, out var engine)) return false;
        
        var span = engine.Get(key);
        if (span.IsEmpty) return false;

        // TTL Kontrolü
        long expireTicks = BitConverter.ToInt64(span.Slice(0, 8));
        if (expireTicks > 0 && DateTime.UtcNow.Ticks > expireTicks)
        {
            engine.Delete(key);
            return false;
        }

        return true;
    }

    public long Size(string cache)
    {
        return _engines.TryGetValue(cache, out var engine) ? engine.Count : 0;
    }
    
    public DateTime LastReload(string cache)
    {
        return _lastReloads.TryGetValue(cache, out var dt) ? dt : DateTime.UtcNow;
    }

    private MmapCacheDefinition<TValue> GetDef<TValue>(string name)
        => _defs.TryGetValue(name, out var obj) && obj is MmapCacheDefinition<TValue> def
            ? def : throw new KeyNotFoundException($"Cache '{name}' not registered or TValue mismatch.");

    /// <summary>
    /// Cache'i tamamen temizler ve verileri Supplier'dan baştan çeker.
    /// </summary>
    public Task ReloadAsync<TValue>(string cache, CancellationToken ct = default)
    {
        if (!_engines.TryGetValue(cache, out var engine)) return Task.CompletedTask;
        var def = GetDef<TValue>(cache);

        // Diskteki ve RAM'deki her şeyi sil
        engine.Clear();

        // Baştan çek
        LoadFromSupplier(def, engine, ct);
        _lastReloads[cache] = DateTime.UtcNow;

        return Task.CompletedTask;
    }

    private void LoadFromSupplier<TValue>(MmapCacheDefinition<TValue> def, LsmEngine engine, CancellationToken ct = default)
    {
        foreach (var kvp in def.Supplier())
        {
            ct.ThrowIfCancellationRequested();
            
            byte[] valBytes = def.Serializer(kvp.Value);
            long expireTicks = def.Ttl > TimeSpan.Zero ? (DateTime.UtcNow + def.Ttl).Ticks : 0;
            
            byte[] payload = new byte[8 + valBytes.Length];
            BitConverter.TryWriteBytes(payload, expireTicks);
            valBytes.CopyTo(payload.AsSpan(8));
            
            engine.Put(kvp.Key, payload);
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var engine in _engines.Values)
        {
            engine.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}