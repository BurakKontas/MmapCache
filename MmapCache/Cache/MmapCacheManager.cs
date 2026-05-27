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

    /// <summary>
    /// Tracks the current active version (N) for each cache.
    /// Engine lives at {basePath}/{cacheName}_v{N}.
    /// On each ReloadAsync the version increments: old engine stays alive for in-flight reads,
    /// new engine is fully loaded, then the pointer is atomically swapped.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _engineVersions = new(StringComparer.Ordinal);

    private MmapCacheManager(string basePath)
    {
        _basePath = basePath;
        Directory.CreateDirectory(basePath);
    }

    // ── Directory helpers ─────────────────────────────────────────────────────

    /// <summary>Physical directory for a specific cache version.</summary>
    private string GetEngineDir(string cacheName, int version) =>
        Path.Combine(_basePath, $"{cacheName}_v{version}");

    /// <summary>
    /// Scans for the highest existing versioned directory so that a warm restart
    /// automatically resumes from the latest persisted data without calling the supplier.
    /// </summary>
    private int FindLatestVersion(string cacheName)
    {
        int version = 0;
        // Walk forward until there is no next version on disk.
        while (Directory.Exists(GetEngineDir(cacheName, version + 1)))
            version++;
        return version;
    }

    // ── Registration ──────────────────────────────────────────────────────────

    public void Register<TValue>(MmapCacheDefinition<TValue> def)
    {
        _defs[def.Name] = def;

        // Pick up the highest existing version so warm restarts skip the supplier.
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

    // ── Read operations ───────────────────────────────────────────────────────

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
                isExpired = true;
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

    // ── Write operations ──────────────────────────────────────────────────────

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

    // ── Zero-downtime versioned reload ────────────────────────────────────────

    /// <summary>
    /// Reloads a cache with zero read downtime using versioned engine swap:
    /// <list type="number">
    ///   <item>Creates a new LsmEngine at <c>{cacheName}_v{N+1}</c>.</item>
    ///   <item>Fully populates it from the supplier (reads still served by the current engine).</item>
    ///   <item>Atomically swaps <c>_engines[cache]</c> to the new engine.</item>
    ///   <item>Retires the old engine after a 250 ms grace period for in-flight reads.</item>
    /// </list>
    /// There is NO window where the cache returns empty data.
    /// </summary>
    public async Task ReloadAsync<TValue>(string cache, CancellationToken ct = default)
    {
        if (!_engines.TryGetValue(cache, out var oldEngine)) return;
        var def = GetDef<TValue>(cache);

        int oldVersion = _engineVersions.GetOrAdd(cache, 0);
        int newVersion = oldVersion + 1;
        string newDir = GetEngineDir(cache, newVersion);

        // ── Phase 1: build shadow engine while old engine keeps serving reads ──
        var newEngine = new LsmEngine(newDir,
            flushThresholdBytes: def.MemTableFlushThresholdBytes,
            radixTreeCapacity: def.RadixTreeCapacity);

        try
        {
            await Task.Run(() => LoadFromSupplier(def, newEngine, ct), ct);
        }
        catch
        {
            // If population fails, discard the shadow engine and leave old one intact.
            newEngine.Dispose();
            try { if (Directory.Exists(newDir)) Directory.Delete(newDir, recursive: true); } catch { }
            throw;
        }

        // ── Phase 2: atomic pointer swap ─────────────────────────────────────
        // From this point new readers go to newEngine.
        // Threads that already captured oldEngine's reference continue safely.
        _engines[cache] = newEngine;
        _engineVersions[cache] = newVersion;
        _lastReloads[cache] = DateTime.UtcNow;

        // ── Phase 3: retire old engine with a grace period ────────────────────
        // 250 ms is enough for any in-flight TryGet/Get to finish under _engineLock.
        string oldDir = GetEngineDir(cache, oldVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(250, CancellationToken.None);
            oldEngine.Dispose();
            try { if (Directory.Exists(oldDir)) Directory.Delete(oldDir, recursive: true); } catch { }
        });
    }

    private void LoadFromSupplier<TValue>(MmapCacheDefinition<TValue> def, LsmEngine engine, CancellationToken ct = default)
    {
        var data = def.Supplier();
        if (data == null) return;

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

    // ── Key enumeration ───────────────────────────────────────────────────────

    public IEnumerable<string> ScanKeys(string cacheName, string prefix = "")
    {
        if (!_engines.TryGetValue(cacheName, out var engine))
            return Array.Empty<string>();

        return engine.EnumerateKeys(prefix);
    }

    public void ScanKeysZeroAlloc(string cacheName, RadixKeySpanConsumer consumer, string prefix = "")
    {
        if (!_engines.TryGetValue(cacheName, out var engine))
            return;

        engine.ScanKeysZeroAlloc(consumer, prefix);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        foreach (var engine in _engines.Values)
        {
            engine.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MmapCacheDefinition<TValue> GetDef<TValue>(string cache)
    {
        if (!_defs.TryGetValue(cache, out var def))
            throw new KeyNotFoundException($"Cache definition '{cache}' not found.");
        return (MmapCacheDefinition<TValue>)def;
    }
}