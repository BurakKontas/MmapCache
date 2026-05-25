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
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));
        Assert.Same(mgr, MmapCacheManager.Instance);
    }

    [Fact]
    public void Initialize_CalledTwice_ThrowsInvalidOperationException()
    {
        using var tmp = new TempCacheDir();
        MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));

        Assert.Throws<InvalidOperationException>(() =>
            MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1)));
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
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));
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
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));
        mgr.Register(TestFactory.WidgetDef("widgets", count: 5));

        Assert.Null(mgr.Get<Widget>("widgets", "widgets_999"));
    }

    [Fact]
    public void Get_UnregisteredCache_ReturnsNull()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));

        Assert.Null(mgr.Get<Widget>("no-such-cache", "any-key"));
    }

    // ── TryGet ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_ExistingKey_ReturnsTrueAndValue()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));
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
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));
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
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));
        mgr.Register(TestFactory.WidgetDef("w", count));

        Assert.Equal(count, mgr.Size("w"));
    }

    [Fact]
    public void Size_UnregisteredCache_ReturnsZero()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));

        Assert.Equal(0L, mgr.Size("ghost"));
    }

    // ── ReloadAsync concurrency test ─────────────────────────────────────────

    [Fact]
    public async Task ContinuousWrites_WhileConcurrentReadsRunning()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path, ttlCheck: TimeSpan.FromHours(1));
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
                // İlk çağrıda 1 widget, ikinci çağrıda 2 widget dönsün
                if (supplierCalls == 1) return TestFactory.MakeWidgets(1, "w");
                return TestFactory.MakeWidgets(2, "w");
            },
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
        });

        // İlk register işlemi Supplier'ı çalıştırdı
        Assert.Equal(1, supplierCalls);
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));

        // Manuel olarak dışarıdan bir data ekleyelim
        mgr.Put("w", "manual_key", new Widget("manual_key", "Manual", 9.99m, 5));
        Assert.NotNull(mgr.Get<Widget>("w", "manual_key"));

        // RELOAD TETİKLE (Motoru tamamen temizleyip supplier'dan baştan çekecek)
        await mgr.ReloadAsync<Widget>("w");

        // Supplier 2. kez çağrılmış olmalı
        Assert.Equal(2, supplierCalls);

        // w_0 ve yeni eklenen w_1 artık var olmalı
        Assert.NotNull(mgr.Get<Widget>("w", "w_0"));
        Assert.NotNull(mgr.Get<Widget>("w", "w_1"));

        // Manuel eklediğimiz data "Clear" ile uçmuş olmalı
        Assert.Null(mgr.Get<Widget>("w", "manual_key"));
    }

    [Fact]
    public async Task Put_WithTtl_LazyExpiresCorrectly()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        // Default TTL'i 1 saat olan bir tanım
        mgr.Register(TestFactory.WidgetDef("w", 0, ttl: TimeSpan.FromHours(1)));

        // Özel TTL vererek Put yap (50 ms)
        mgr.Put("w", "expire_me", new Widget("1", "Test", 1m, 1), TimeSpan.FromMilliseconds(50));
        
        // Data girildi, var olmalı
        var widget = mgr.Get<Widget>("w", "expire_me");
        Assert.NotNull(widget);

        // 100 ms bekle (Sürenin dolduğundan emin olmak için)
        await Task.Delay(100);

        // Lazy Expiration tetiklenmeli
        widget = mgr.Get<Widget>("w", "expire_me");
        Assert.Null(widget); // Süresi dolduğu için null gelmeli

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

        Assert.Equal(1, supplierCalls); // Should NOT have incremented!
        
        var val2 = mgr2.Get<Widget>("w", "w_0");
        Assert.NotNull(val2);
    }
}