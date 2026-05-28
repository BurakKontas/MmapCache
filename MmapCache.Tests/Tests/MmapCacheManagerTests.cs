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

    // ── Size ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void Size_MatchesSuppliedCount(int count)
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count));

        Assert.Equal((long)count, mgr.Size("w"));
    }

    [Fact]
    public void Size_UnregisteredCache_ReturnsZero()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        Assert.Equal(0L, mgr.Size("ghost"));
    }

    // ── Concurrent writes + reads ─────────────────────────────────────────────

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

        for (int i = 0; i < 1000; i++)
        {
            mgr.Put("w", $"w_{rng.Next(200)}", new Widget($"w_{rng.Next(200)}", $"Updated {i}", 1.99m, 10));
        }
        await Task.Delay(50);

        Volatile.Write(ref reading, false);
        await reader;

        Assert.True(hits > 0);
    }

    // ── ReloadAsync: correctness ──────────────────────────────────────────────

    [Fact]
    public async Task ReloadAsync_LoadsNewDataFromSupplier()
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
                if (supplierCalls == 1) return TestFactory.MakeWidgets(1, "w");
                return TestFactory.MakeWidgets(2, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        Assert.Equal(1, supplierCalls);
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));

        // Insert a manual key that should NOT survive reload (new engine starts fresh)
        mgr.Put("w", "manual_key", new Widget("manual_key", "Manual", 9.99m, 5));
        Assert.NotNull(mgr.Get<Widget>("w", "manual_key"));

        await mgr.ReloadAsync<Widget>("w");

        Assert.Equal(2, supplierCalls);

        // New supplier data is present
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
        Assert.NotNull(mgr.Get<Widget>("w", "w_1"));

        // Manual key is gone — new engine was built from scratch via the supplier
        Assert.Null(mgr.Get<Widget>("w", "manual_key"));
    }

    // ── ReloadAsync: zero-downtime guarantee ──────────────────────────────────

    /// <summary>
    /// Fires concurrent reads while multiple ReloadAsync calls are in-flight.
    /// The supplier has an artificial delay to maximise the overlap window.
    /// No read must ever return null for a key that existed before the reload.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_ConcurrentReads_NeverReturnEmpty()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        int supplierCalls = 0;

        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "w",
            Supplier = () =>
            {
                Interlocked.Increment(ref supplierCalls);
                // Simulate a slow data source so the reload window is wide.
                Thread.Sleep(80);
                return TestFactory.MakeWidgets(50, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        // Key that must be readable throughout every reload cycle.
        const string probeKey = "w_0";

        var cts = new CancellationTokenSource();
        int nullReads = 0;
        int totalReads = 0;

        // Continuous reader — must never observe a null for the probe key.
        var readerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = mgr.Get<Widget>("w", probeKey);
                Interlocked.Increment(ref totalReads);
                if (result is null)
                    Interlocked.Increment(ref nullReads);
            }
        }, CancellationToken.None);

        // Trigger 3 successive reloads while the reader is running.
        for (int i = 0; i < 3; i++)
        {
            await mgr.ReloadAsync<Widget>("w");
            await Task.Delay(20); // tiny gap so reloads don't perfectly serialise
        }

        cts.Cancel();
        await readerTask;

        Assert.True(totalReads > 0, "Reader must have executed at least one read.");
        Assert.Equal(0, nullReads);                // ← the key guarantee: zero empty windows
        Assert.Equal(4, supplierCalls);            // 1 initial + 3 reloads
    }

    /// <summary>
    /// After a successful ReloadAsync the new engine's data is immediately visible
    /// and the LastReload timestamp advances.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_UpdatesLastReloadTimestamp()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 5));

        var before = mgr.LastReload("w");
        await Task.Delay(10);

        await mgr.ReloadAsync<Widget>("w");

        Assert.True(mgr.LastReload("w") > before);
    }

    /// <summary>
    /// If the supplier throws during reload, the old engine must remain intact
    /// and continue to serve data (no data loss on failure).
    /// </summary>
    [Fact]
    public async Task ReloadAsync_SupplierThrows_OldEngineRemainsIntact()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        int callCount = 0;

        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "w",
            Supplier = () =>
            {
                callCount++;
                if (callCount > 1) throw new InvalidOperationException("Supplier exploded");
                return TestFactory.MakeWidgets(10, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        // Sanity: data is there before the failing reload.
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.ReloadAsync<Widget>("w"));

        // Old engine is still serving data after the failed reload.
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
        Assert.Equal(10L, mgr.Size("w"));
    }

    /// <summary>
    /// Verifies that the versioned directory layout is correct:
    /// after two reloads the active version is v2 and the old v0/v1 dirs are cleaned up.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_VersionedDirs_OldDirsRemovedAfterGracePeriod()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 5));

        // v0 must exist after register.
        Assert.True(Directory.Exists(Path.Combine(tmp.Path, "w_v0")));

        await mgr.ReloadAsync<Widget>("w");

        // v1 created; give grace period enough time to clean v0.
        await Task.Delay(400);
        Assert.True(Directory.Exists(Path.Combine(tmp.Path, "w_v1")), "v1 must exist after first reload");
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "w_v0")), "v0 must be cleaned up after grace period");

        await mgr.ReloadAsync<Widget>("w");

        await Task.Delay(400);
        Assert.True(Directory.Exists(Path.Combine(tmp.Path, "w_v2")), "v2 must exist after second reload");
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "w_v1")), "v1 must be cleaned up after grace period");
    }

    // ── ReloadAsync: concurrent reload guard ──────────────────────────────────

    /// <summary>
    /// Two simultaneous ReloadAsync calls for the same cache: the second one must
    /// throw InvalidOperationException immediately, while the first completes normally.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_ConcurrentReload_SecondThrowsAlreadyReloading()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        // Slow supplier so the first reload is still in-flight when the second starts.
        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "w",
            Supplier = () =>
            {
                Thread.Sleep(200);
                return TestFactory.MakeWidgets(5, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        // Fire the first reload and immediately attempt a second one.
        var first = mgr.ReloadAsync<Widget>("w");
        var second = mgr.ReloadAsync<Widget>("w"); // must throw without waiting

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Contains("already being reloaded", ex.Message);

        // First reload must still succeed.
        await first;
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
    }

    /// <summary>
    /// Different caches are independent: two simultaneous reloads for different
    /// caches must both succeed without interfering with each other.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_ConcurrentReload_DifferentCaches_BothSucceed()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "a",
            Supplier = () => { Thread.Sleep(100); return TestFactory.MakeWidgets(3, "a"); },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });
        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "b",
            Supplier = () => { Thread.Sleep(100); return TestFactory.MakeWidgets(3, "b"); },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        // Both reloads in flight at the same time — different caches, must not block each other.
        await Task.WhenAll(
            mgr.ReloadAsync<Widget>("a"),
            mgr.ReloadAsync<Widget>("b"));

        Assert.NotNull(mgr.Get<Widget>("a", "a_0"));
        Assert.NotNull(mgr.Get<Widget>("b", "b_0"));
    }

    /// <summary>
    /// After a reload finishes (or is cancelled), the guard must be released so a
    /// subsequent reload can start without throwing.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_AfterFirstReloadFinishes_SecondReloadSucceeds()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 5));

        await mgr.ReloadAsync<Widget>("w");  // first
        await mgr.ReloadAsync<Widget>("w");  // second — guard must have been released

        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
    }

    /// <summary>
    /// Even when the first reload is cancelled, the guard must be released so the
    /// next call can proceed.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_AfterCancelledReload_GuardIsReleased_NextReloadSucceeds()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 5));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mgr.ReloadAsync<Widget>("w", cts.Token));

        // Guard released — this must not throw "already reloading".
        await mgr.ReloadAsync<Widget>("w");
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
    }

    // ── ReloadAsync: cancellation ─────────────────────────────────────────────

    /// <summary>
    /// Cancelling before the supplier starts: the new versioned directory must not
    /// exist and the old engine must continue serving data unchanged.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_CancelledBeforeSupplierStarts_OldEngineIntact_NewDirAbsent()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 5));

        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));

        // Cancel before calling ReloadAsync — token is already cancelled.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mgr.ReloadAsync<Widget>("w", cts.Token));

        // Old engine is intact.
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
        Assert.Equal(5L, mgr.Size("w"));

        // Shadow directory was cleaned up (or was never created).
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "w_v1")),
            "w_v1 must be absent after a cancelled reload");
    }

    /// <summary>
    /// Cancelling mid-supplier: the reload was cancelled while the supplier was
    /// writing records. The new version directory must be removed and the old
    /// engine must be left fully intact.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_CancelledMidSupplier_OldEngineIntact_NewDirCleaned()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        using var cts = new CancellationTokenSource();
        int supplierCalls = 0;

        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "w",
            Supplier = () =>
            {
                Interlocked.Increment(ref supplierCalls);
                if (supplierCalls == 1)
                    return TestFactory.MakeWidgets(5, "w");

                // On the reload call cancel mid-enumeration so that
                // ThrowIfCancellationRequested() inside LoadFromSupplier fires.
                cts.Cancel();
                return TestFactory.MakeWidgets(100, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        Assert.Equal(1, supplierCalls);
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mgr.ReloadAsync<Widget>("w", cts.Token));

        // Old engine still serves data.
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
        Assert.Equal(5L, mgr.Size("w"));

        // New versioned directory must have been cleaned up.
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "w_v1")),
            "w_v1 must be cleaned up after a mid-supplier cancellation");
    }

    /// <summary>
    /// A cancelled reload must not advance _engineVersions: the next successful
    /// ReloadAsync must still produce v1, not v2.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_CancelledReload_DoesNotAdvanceVersion()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", count: 5));

        // v0 exists after initial Register.
        Assert.True(Directory.Exists(Path.Combine(tmp.Path, "w_v0")));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mgr.ReloadAsync<Widget>("w", cts.Token));

        // Version counter must NOT have moved — the next real reload produces v1.
        await mgr.ReloadAsync<Widget>("w");
        await Task.Delay(400); // grace-period for old dir cleanup

        Assert.True(Directory.Exists(Path.Combine(tmp.Path, "w_v1")),
            "First successful reload must land at v1, not v2");
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "w_v0")),
            "v0 must be cleaned up after the successful reload");
    }

    // ── TTL / expiry ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_WithTtl_LazyExpiresCorrectly()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);
        mgr.Register(TestFactory.WidgetDef("w", 0, ttl: TimeSpan.FromHours(1)));

        mgr.Put("w", "expire_me", new Widget("1", "Test", 1m, 1), TimeSpan.FromMilliseconds(50));

        Assert.NotNull(mgr.Get<Widget>("w", "expire_me"));

        await Task.Delay(120);

        Assert.Null(mgr.Get<Widget>("w", "expire_me"));
        Assert.False(mgr.Exists("w", "expire_me"));
    }

    // ── Warm restart ──────────────────────────────────────────────────────────

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
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));

        await mgr.DisposeAsync();
        SingletonReset.Reset();

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

        // Supplier must NOT have been called again — data recovered from SST files.
        Assert.Equal(1, supplierCalls);
        Assert.NotNull(mgr2.Get<Widget>("w", "w_0"));
    }

    // ── ScanKeys ──────────────────────────────────────────────────────────────

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
        Assert.Contains("scan_1", keys);
    }

    [Fact]
    public void ScanKeys_ReturnsEmpty_ForUnknownCache()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        Assert.Empty(mgr.ScanKeys("ghost"));
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
        mgr.ScanKeysZeroAlloc("zscan", span => seen.Add(span.ToString()));

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
        mgr.ScanKeysZeroAlloc("zscan", span => seen.Add(span.ToString()), prefix: "zscan_4");

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
                mgr.Put("cscan", $"cscan_{rnd.Next(200)}", new Widget("x", "y", 1, 1));
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