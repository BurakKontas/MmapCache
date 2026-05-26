using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MmapCache.Config;
using MmapCache.Lsm;

namespace MmapCache.Cache;

public sealed class MmapCacheManager : IAsyncDisposable
{
    private static MmapCacheManager? _instance;

    public static MmapCacheManager Initialize(string basePath)
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

        var engine = new LsmEngine(engineDir, flushThresholdBytes: def.MemTableFlushThresholdBytes, radixTreeCapacity: def.RadixTreeCapacity);
        _engines[def.Name] = engine;

        bool recovered = engine.Count > 0;

        if (!recovered)
        {
            var data = def.Supplier();
            if (data != null)
            {
                foreach (var kvp in data)
                {
                    byte[] valBytes = def.Serializer(kvp.Value);

                    long expireTicks = def.Ttl > TimeSpan.Zero
                        ? (DateTime.UtcNow + def.Ttl).Ticks
                        : 0;

                    byte[] payload = new byte[8 + valBytes.Length];

                    BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), expireTicks);
                    valBytes.CopyTo(payload.AsSpan(8));

                    engine.Put(kvp.Key, payload);
                }
            }
        }

        _lastReloads[def.Name] = DateTime.UtcNow;
    }

    public long Size(string cache)
    {
        var result = _engines.TryGetValue(cache, out var engine);
        if (result == false) return 0L;
        return engine?.Count ?? 0L;
    }

    public bool Exists(string cache, string key)
    {
        if (!_engines.TryGetValue(cache, out var engine)) return false;

        bool isExpired = false;

        bool found = engine.TryGet(key, bytes =>
        {
            if (bytes.Length < 8) return;

            long expireTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(0, 8));
            if (expireTicks > 0 && DateTime.UtcNow.Ticks > expireTicks)
            {
                isExpired = true;
            }
        });

        if (isExpired)
        {
            engine.Delete(key);
            return false;
        }

        return found;
    }

    public TValue? Get<TValue>(string cache, string key)
    {
        if (!_engines.TryGetValue(cache, out var engine)) return default;

        var def = GetDef<TValue>(cache);
        TValue? result = default;
        bool isExpired = false;

        bool found = engine.TryGet(key, bytes =>
        {
            if (bytes.Length < 8) return;

            long expireTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(0, 8));
            if (expireTicks > 0 && DateTime.UtcNow.Ticks > expireTicks)
            {
                isExpired = true;
                return;
            }

            var valueBytes = bytes.Slice(8);
            result = def.Deserializer(valueBytes);
        });

        if (isExpired)
        {
            engine.Delete(key);
            return default;
        }

        return result;
    }

    public bool TryGet<TValue>(string cache, string key, out TValue? value)
    {
        var result = Get<TValue>(cache, key);
        if (result == null)
        {
            value = default;
            return false;
        }
        value = result;
        return true;
    }

    public void Put<TValue>(string cache, string key, TValue value, TimeSpan? customTtl = null)
    {
        if (!_engines.TryGetValue(cache, out var engine))
            return;

        var def = GetDef<TValue>(cache);

        byte[] valBytes = def.Serializer(value)
            ?? throw new InvalidOperationException("Serializer returned null");

        long expireTicks;
        var ttl = customTtl ?? def.Ttl;

        if (ttl <= TimeSpan.Zero)
        {
            expireTicks = 0;
        }
        else
        {
            expireTicks = DateTime.UtcNow.Ticks + ttl.Ticks;
        }

        byte[] payload = new byte[8 + valBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), expireTicks);
        valBytes.CopyTo(payload.AsSpan(8));

        engine.Put(key, payload);
    }

    public void Delete(string cache, string key)
    {
        if (!_engines.TryGetValue(cache, out var engine)) return;
        engine.Delete(key);
    }

    public DateTime LastReload(string cache)
    {
        return _lastReloads.TryGetValue(cache, out var dt) ? dt : DateTime.MinValue;
    }

    private MmapCacheDefinition<TValue> GetDef<TValue>(string cache)
    {
        if (!_defs.TryGetValue(cache, out var def))
            throw new KeyNotFoundException($"Cache definition '{cache}' not found.");
        return (MmapCacheDefinition<TValue>)def;
    }

    public Task ReloadAsync<TValue>(string cache, CancellationToken ct = default)
    {
        if (!_engines.TryGetValue(cache, out var engine)) return Task.CompletedTask;
        var def = GetDef<TValue>(cache);

        engine.ForceClearWalAndIndex();

        LoadFromSupplier(def, engine, ct);
        _lastReloads[cache] = DateTime.UtcNow;

        return Task.CompletedTask;
    }

    private void LoadFromSupplier<TValue>(MmapCacheDefinition<TValue> def, LsmEngine engine, CancellationToken ct = default)
    {
        var data = def.Supplier();

        if (data == null)
            return;

        foreach (var kvp in data)
        {
            ct.ThrowIfCancellationRequested();

            byte[] valBytes = def.Serializer(kvp.Value);
            long expireTicks = def.Ttl > TimeSpan.Zero
                ? (DateTime.UtcNow + def.Ttl).Ticks
                : 0;

            byte[] payload = new byte[8 + valBytes.Length];

            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), expireTicks);
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