using MmapCache.Cache;
using MmapCache.Config;
using MmapCache.Tests.Helpers;

namespace MmapCache.Tests.Tests;

[Collection("Sequential")]
public sealed class MmapCacheManagerTests
{
    // ── Singleton lifecycle ───────────────────────────────────────────────────

    [Fact]
    public void Initialize_ReturnsSameInstanceAsProperty()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        Assert.Same(mgr, MmapCacheManager.Instance);
    }

    [Fact]
    public void Initialize_CalledTwice_ThrowsInvalidOperationException()
    {
        using var tmp = new TempCacheDir();
        MmapCacheManager.Initialize(tmp.Path);

        Assert.Throws<InvalidOperationException>(() =>
            MmapCacheManager.Initialize(tmp.Path));
    }

    [Fact]
    public void Instance_BeforeInitialize_ThrowsInvalidOperationException()
    {
        using var tmp = new TempCacheDir();
        Assert.Throws<InvalidOperationException>(() => _ = MmapCacheManager.Instance);
    }

    // ── Register + basic reads ────────────────────────────────────────────────

    [Fact]
    public void Get_RegisteredKey_ReturnsValue()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("widgets", count: 10));

        var w = mgr.Get<Widget>("widgets", "widgets_5");

        Assert.NotNull(w);
        Assert.Equal("widgets_5", w!.Id);
        Assert.Equal("Label #5", w.Label);
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("widgets", count: 5));

        Assert.Null(mgr.Get<Widget>("widgets", "widgets_999"));
    }

    [Fact]
    public void Get_UnregisteredCache_ReturnsNull()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        Assert.Null(mgr.Get<Widget>("no-such-cache", "any-key"));
    }

    // ── TryGet ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_ExistingKey_ReturnsTrueAndValue()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 20));

        var found = mgr.TryGet<Widget>("w", "w_0", out var widget);

        Assert.True(found);
        Assert.NotNull(widget);
        Assert.Equal("w_0", widget!.Id);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalseAndDefault()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 5));

        var found = mgr.TryGet<Widget>("w", "w_999", out var widget);

        Assert.False(found);
        Assert.Null(widget);
    }

    // ── Size (Validates Unmanaged Trie Live Record Counts) ────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void Size_MatchesSuppliedCount(int count)
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count));

        // Now maps perfectly to _index.Count from the native radix tree layout
        Assert.Equal((long)count, mgr.Size("w"));
    }

    [Fact]
    public void Size_UnregisteredCache_ReturnsZero()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        Assert.Equal(0L, mgr.Size("ghost"));
    }

    // ── ReloadAsync concurrency test ─────────────────────────────────────────

    [Fact]
    public async Task ContinuousWrites_WhileConcurrentReadsRunning()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 200));

        bool reading = true;
        int hits = 0;
        var rng = new Random(42);

        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref reading))
            {
                var result = mgr.Get<Widget>("w", $"w_{rng.Next(200)}");
                if (result is not null)
                    Interlocked.Increment(ref hits);
            }
        });

        // Mutate rapidly simulating foreground changes via LSM Put 
        for (int i = 0; i < 1000; i++)
        {
            mgr.Put("w", $"w_{rng.Next(200)}", new Widget($"w_{rng.Next(200)}", $"Updated {i}", 1.99m, 10));
        }
        await Task.Delay(50); // Let it read a bit

        Volatile.Write(ref reading, false);
        await reader;

        Assert.True(hits > 0);
    }

    [Fact]
    public async Task ReloadAsync_ClearsExistingAndLoadsFromSupplier()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        int supplierCalls = 0;

        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "w",
            Supplier = () =>
            {
                supplierCalls++;
                // First call yields 1 item, second call yields 2 items
                if (supplierCalls == 1) return TestFactory.MakeWidgets(1, "w");
                return TestFactory.MakeWidgets(2, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        // Initial registration should execute the supplier once
        Assert.Equal(1, supplierCalls);
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));

        // Insert an ephemeral record manually
        mgr.Put("w", "manual_key", new Widget("manual_key", "Manual", 9.99m, 5));
        Assert.NotNull(mgr.Get<Widget>("w", "manual_key"));

        // RELOAD: Wipe the engine state completely and refresh from the updated supplier stream
        await mgr.ReloadAsync<Widget>("w");

        // Supplier must be invoked for the 2nd time
        Assert.Equal(2, supplierCalls);

        // Verified that new values exist in the freshly built unmanaged arena
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
        Assert.NotNull(mgr.Get<Widget>("w", "w_1"));

        // The manual key must be cleared out cleanly by the atomic reset execution
        Assert.Null(mgr.Get<Widget>("w", "manual_key"));
    }

    [Fact]
    public async Task Put_WithTtl_LazyExpiresCorrectly()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(TestFactory.WidgetDef("w", 0, ttl: TimeSpan.FromHours(1)));

        // Write with a very short custom TTL (50 milliseconds)
        mgr.Put("w", "expire_me", new Widget("1", "Test", 1m, 1), TimeSpan.FromMilliseconds(50));

        // Value must be fully retrievable immediately after insertion
        var widget = mgr.Get<Widget>("w", "expire_me");
        Assert.NotNull(widget);

        // Sleep past the TTL window boundary to guarantee expiration state
        await Task.Delay(120);

        // Lazy Expiration mechanism triggers here
        widget = mgr.Get<Widget>("w", "expire_me");
        Assert.Null(widget); // Must be dead/null

        bool exists = mgr.Exists("w", "expire_me");
        Assert.False(exists);
    }

    [Fact]
    public async Task WarmRestart_ResumesFromDiskWithoutCallingSupplier()
    {
        using var tmp = new TempCacheDir();
        int supplierCalls = 0;

        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "w",
            Supplier = () =>
            {
                Interlocked.Increment(ref supplierCalls);
                return TestFactory.MakeWidgets(5, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        Assert.Equal(1, supplierCalls);
        var val1 = mgr.Get<Widget>("w", "w_0");
        Assert.NotNull(val1);

        // Unmap memory structures and completely drop current live engine reference
        await mgr.DisposeAsync();
        SingletonReset.Reset();

        // Spin up a brand new manager instance bound to the exact same physical directory path
        var mgr2 = MmapCacheManager.Initialize(tmp.Path);

        mgr2.Register(new MmapCacheDefinition<Widget>
        {
            Name = "w",
            Supplier = () =>
            {
                Interlocked.Increment(ref supplierCalls);
                return TestFactory.MakeWidgets(5, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        // CRITICAL CHECK: The supplier counter MUST remain 1 because LsmEngine restored everything 
        // straight from the existing unmanaged SSTables on disk during the bootstrap process!
        Assert.Equal(1, supplierCalls);

        var val2 = mgr2.Get<Widget>("w", "w_0");
        Assert.NotNull(val2);
    }

    [Fact]
    public void ScanKeys_ReturnsAllKeys_WhenPrefixEmpty()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(TestFactory.WidgetDef("scan", count: 10));

        var keys = mgr.ScanKeys("scan").ToList();

        Assert.Equal(10, keys.Count);
        Assert.Contains("scan_0", keys);
        Assert.Contains("scan_9", keys);
    }

    [Fact]
    public void ScanKeys_ReturnsFilteredKeys_WithPrefix()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(TestFactory.WidgetDef("scan", count: 20));

        var keys = mgr.ScanKeys("scan", "scan_1").ToList();

        Assert.All(keys, k => Assert.StartsWith("scan_1", k));

        // örn: scan_1, scan_10-19
        Assert.Contains("scan_1", keys);
    }

    [Fact]
    public void ScanKeys_ReturnsEmpty_ForUnknownCache()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        var keys = mgr.ScanKeys("ghost").ToList();

        Assert.Empty(keys);
    }

    [Fact]
    public void ScanKeys_ReflectsInsertedKeys_Dynamically()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(TestFactory.WidgetDef("dyn", count: 5));

        mgr.Put("dyn", "extra_key_1", new Widget("extra_key_1", "x", 1, 1));
        mgr.Put("dyn", "extra_key_2", new Widget("extra_key_2", "x", 1, 1));

        var keys = mgr.ScanKeys("dyn").ToList();

        Assert.Contains("extra_key_1", keys);
        Assert.Contains("extra_key_2", keys);
    }

    [Fact]
    public void ScanKeysZeroAlloc_InvokesConsumer_ForAllKeys()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(TestFactory.WidgetDef("zscan", count: 15));

        var seen = new HashSet<string>();

        mgr.ScanKeysZeroAlloc("zscan", span =>
        {
            seen.Add(span.ToString());
        });

        Assert.Equal(15, seen.Count);
        Assert.Contains("zscan_0", seen);
        Assert.Contains("zscan_14", seen);
    }

    [Fact]
    public void ScanKeysZeroAlloc_FiltersByPrefix()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(TestFactory.WidgetDef("zscan", count: 50));

        var seen = new List<string>();

        mgr.ScanKeysZeroAlloc("zscan", span =>
        {
            seen.Add(span.ToString());
        }, prefix: "zscan_4");

        Assert.All(seen, k => Assert.StartsWith("zscan_4", k));
        Assert.Contains("zscan_4", seen);
    }

    [Fact]
    public void ScanKeys_IsSafe_DuringConcurrentWrites()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(TestFactory.WidgetDef("cscan", count: 200));

        var running = true;
        int scanHits = 0;

        var writer = Task.Run(() =>
        {
            var rnd = new Random(1);

            while (Volatile.Read(ref running))
            {
                mgr.Put("cscan", $"cscan_{rnd.Next(200)}",
                    new Widget("x", "y", 1, 1));
            }
        });

        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref running))
            {
                foreach (var k in mgr.ScanKeys("cscan"))
                {
                    if (k != null)
                        Interlocked.Increment(ref scanHits);
                }
            }
        });

        Task.Delay(100).Wait();

        Volatile.Write(ref running, false);

        Task.WaitAll(writer, reader);

        Assert.True(scanHits > 0);
    }
}