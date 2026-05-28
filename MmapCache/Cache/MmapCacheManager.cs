using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        if (_instance is not null)
            throw new InvalidOperationException("Already initialized.");

        _instance = new MmapCacheManager(basePath);
        return _instance;
    }

    public static MmapCacheManager Instance =>
        _instance ?? throw new InvalidOperationException("Call Initialize() first.");

    private readonly string _basePath;

    private readonly ConcurrentDictionary<string, ICacheDefinition> _defs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LsmEngine> _engines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastReloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _engineVersions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeReloads = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _shutdownCts = new();

    public CancellationToken ShutdownToken => _shutdownCts.Token;

    private MmapCacheManager(string basePath)
    {
        _basePath = basePath;
        Directory.CreateDirectory(basePath);
    }

    private string GetEngineDir(string cacheName, int version) =>
        Path.Combine(_basePath, $"{cacheName}_v{version}");

    private int FindLatestVersion(string cacheName)
    {
        int version = 0;
        while (Directory.Exists(GetEngineDir(cacheName, version + 1)))
            version++;

        return version;
    }

    public void Register<TValue>(MmapCacheDefinition<TValue> def, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _defs[def.Name] = def;

        int version = FindLatestVersion(def.Name);
        _engineVersions[def.Name] = version;

        string engineDir = GetEngineDir(def.Name, version);

        var engine = new LsmEngine(engineDir,
            flushThresholdBytes: def.MemTableFlushThresholdBytes,
            radixTreeCapacity: def.RadixTreeCapacity);

        _engines[def.Name] = engine;

        bool recovered = engine.Count > 0;

        if (!recovered)
        {
            var data = def.Supplier(ct);

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

        _lastReloads[def.Name] = DateTime.UtcNow;
    }

    public long Size(string cache)
        => _engines.TryGetValue(cache, out var engine) ? engine.Count : 0;

    public bool Exists(string cache, string key)
    {
        if (!_engines.TryGetValue(cache, out var engine))
            return false;

        bool expired = false;

        bool found = engine.TryGet(key, bytes =>
        {
            if (bytes.Length < 8) return;

            long expireTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(0, 8));
            if (expireTicks > 0 && DateTime.UtcNow.Ticks > expireTicks)
                expired = true;
        });

        if (expired)
        {
            engine.Delete(key);
            return false;
        }

        return found;
    }

    public TValue? Get<TValue>(string cache, string key)
    {
        if (!_engines.TryGetValue(cache, out var engine))
            return default;

        var def = GetDef<TValue>(cache);

        TValue? result = default;
        bool expired = false;

        bool found = engine.TryGet(key, bytes =>
        {
            if (bytes.Length < 8) return;

            long expireTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(0, 8));
            if (expireTicks > 0 && DateTime.UtcNow.Ticks > expireTicks)
            {
                expired = true;
                return;
            }

            result = def.Deserializer(bytes.Slice(8));
        });

        if (expired)
        {
            engine.Delete(key);
            return default;
        }

        return found ? result : default;
    }

    public bool TryGet<TValue>(string cache, string key, out TValue? value)
    {
        value = Get<TValue>(cache, key);
        return value is not null;
    }

    public void Put<TValue>(string cache, string key, TValue value, TimeSpan? customTtl = null)
    {
        if (!_engines.TryGetValue(cache, out var engine))
            return;

        var def = GetDef<TValue>(cache);

        byte[] valBytes = def.Serializer(value);

        long expireTicks;
        var ttl = customTtl ?? def.Ttl;

        expireTicks = ttl <= TimeSpan.Zero
            ? 0
            : DateTime.UtcNow.Ticks + ttl.Ticks;

        byte[] payload = new byte[8 + valBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), expireTicks);
        valBytes.CopyTo(payload.AsSpan(8));

        engine.Put(key, payload);
    }

    public void Delete(string cache, string key)
    {
        if (_engines.TryGetValue(cache, out var engine))
            engine.Delete(key);
    }

    public DateTime LastReload(string cache)
        => _lastReloads.TryGetValue(cache, out var dt) ? dt : DateTime.MinValue;

    public async Task ReloadAsync<TValue>(string cache, CancellationToken ct = default)
    {
        if (!_engines.TryGetValue(cache, out var oldEngine))
            return;

        var def = GetDef<TValue>(cache);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _shutdownCts.Token);

        var token = linkedCts.Token;

        if (!_activeReloads.TryAdd(cache, 0))
            throw new CacheAlreadyReloadingException(
                $"Cache '{cache}' is already being reloaded.");

        try
        {
            await ReloadCoreAsync(def, cache, oldEngine, token);
        }
        finally
        {
            _activeReloads.TryRemove(cache, out _);
        }
    }

    private async Task ReloadCoreAsync<TValue>(
        MmapCacheDefinition<TValue> def,
        string cache,
        LsmEngine oldEngine,
        CancellationToken ct)
    {
        int oldVersion = _engineVersions.GetOrAdd(cache, 0);
        int newVersion = oldVersion + 1;

        string newDir = GetEngineDir(cache, newVersion);

        var newEngine = new LsmEngine(newDir,
            def.MemTableFlushThresholdBytes,
            def.RadixTreeCapacity);

        try
        {
            await Task.Run(() => LoadFromSupplier(def, newEngine, ct), ct);
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            newEngine.Dispose();

            try { if (Directory.Exists(newDir)) Directory.Delete(newDir, true); } catch { }

            throw;
        }

        _engines[cache] = newEngine;
        _engineVersions[cache] = newVersion;
        _lastReloads[cache] = DateTime.UtcNow;

        string oldDir = GetEngineDir(cache, oldVersion);

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            oldEngine.Dispose();

            try { if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true); } catch { }
        });
    }

    private void LoadFromSupplier<TValue>(
        MmapCacheDefinition<TValue> def,
        LsmEngine engine,
        CancellationToken ct)
    {
        var data = def.Supplier(ct);

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

    public IEnumerable<string> ScanKeys(string cacheName, string prefix = "", CancellationToken ct = default)
    {
        if (!_engines.TryGetValue(cacheName, out var engine))
            return Array.Empty<string>();

        return engine.EnumerateKeys(prefix, ct);
    }

    public void ScanKeysZeroAlloc(string cacheName, RadixKeySpanConsumer consumer, string prefix = "", CancellationToken ct = default)
    {
        if (!_engines.TryGetValue(cacheName, out var engine))
            return;

        engine.ScanKeysZeroAlloc(consumer, prefix, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();

        foreach (var engine in _engines.Values)
            engine.Dispose();

        _shutdownCts.Dispose();

        await ValueTask.CompletedTask;
    }

    private MmapCacheDefinition<TValue> GetDef<TValue>(string cache)
    {
        if (!_defs.TryGetValue(cache, out var def))
            throw new KeyNotFoundException($"Cache definition '{cache}' not found.");

        return (MmapCacheDefinition<TValue>)def;
    }
}