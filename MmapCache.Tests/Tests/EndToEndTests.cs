using System.Text.Json;
using MmapCache.Cache;
using MmapCache.Config;
using MmapCache.Tests.Helpers;
using Xunit.Abstractions;

namespace MmapCache.Tests.Tests;

/// <summary>
/// Full end-to-end scenario
///
/// This test intentionally covers the same narrative as the demo program so
/// that contributors can see at a glance whether the overall flow still works
/// after any change.  Individual unit tests above cover edge cases; this test
/// covers the happy-path integration story.
/// </summary>
[Collection("Sequential")]
public sealed class EndToEndTests(ITestOutputHelper output)
{
private sealed record Product(string Id, string Name, string Category, decimal Price, int Stock);

    private static IEnumerable<(string, Product)> GenerateProducts(int count)
    {
        var categories = new[] { "Electronics", "Books", "Clothing", "Food", "Sports" };
        for (int i = 0; i < count; i++)
        {
            var p = new Product(
                Id: $"product_{i}",
                Name: $"Product #{i:D6}",
                Category: categories[i % categories.Length],
                Price: Math.Round((i % 1000) + 0.99m, 2),
                Stock: i % 500);
            yield return ($"product_{i}", p);
        }
    }

    private static MmapCacheDefinition<Product> ProductDef(int count) => new()
    {
        Name = "products",
        Supplier = () => GenerateProducts(count),
        Serializer = p => JsonSerializer.SerializeToUtf8Bytes(p),
        Deserializer = b => JsonSerializer.Deserialize<Product>(b)!,
        Ttl = TimeSpan.FromMinutes(5),
        DynamicSizing = true,
        L1MaxSize = 1_000,
        L1Ttl = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public async Task FullScenario_LoadReadReloadConcurrent()
    {
        using var tmp = new TempCacheDir();
        const int ProductCount = 5_000;

        // 1. Initialize
        var manager = MmapCacheManager.Initialize(tmp.Path);

        // 2. Load
        manager.Register(ProductDef(ProductCount));

        output.WriteLine($"Loaded — size={manager.Size("products"):N0}  ts={manager.LastReload("products"):HH:mm:ss}");
        Assert.Equal(ProductCount, manager.Size("products"));

        // 3. Spot reads
        string[] testKeys = ["product_0", "product_42", $"product_{ProductCount - 1}", "product_not_exist"];
        foreach (var key in testKeys)
        {
            var p = manager.Get<Product>("products", key);
            if (key == "product_not_exist")
                Assert.Null(p);
            else
                Assert.NotNull(p);
        }

        // 4. Exists checks
        Assert.True(manager.Exists("products", "product_1000"));
        Assert.False(manager.Exists("products", "product_xyz"));

        // 5. Read benchmark
        const int ReadCount = 5_000;
        var rng = new Random(42);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < ReadCount; i++)
            manager.Get<Product>("products", $"product_{rng.Next(ProductCount)}");

        sw.Stop();
        double opsPerSec = ReadCount / sw.Elapsed.TotalSeconds;
        output.WriteLine($"Reads: {ReadCount:N0} in {sw.ElapsedMilliseconds}ms → {opsPerSec:N0} ops/s");
        Assert.True(opsPerSec > 10_000, $"Read throughput too low: {opsPerSec:N0} ops/s");

        // 6. LSM Engine: Reload (Clear & Refetch) while reads are running
        bool reading = true;
        int readsDone = 0;
        int nullsReturned = 0;

        var bgReads = Task.Run(() =>
        {
            var r = new Random(99);
            while (Volatile.Read(ref reading))
            {
                // Engine clear anında çok kısa null dönebilir, bu LSM'in doğası gereğidir
                var result = manager.Get<Product>("products", $"product_{r.Next(ProductCount)}");
                if (result is null) Interlocked.Increment(ref nullsReturned);
                Interlocked.Increment(ref readsDone);
            }
        });

        await manager.ReloadAsync<Product>("products");

        Volatile.Write(ref reading, false);
        await bgReads;

        output.WriteLine($"Reads during reload: {readsDone:N0}");
        Assert.True(readsDone > 0, "Background reader did not execute.");

        // 7. Data still valid after reload
        for (int i = 0; i < Math.Min(100, ProductCount); i++)
        {
            var p = manager.Get<Product>("products", $"product_{i}");
            Assert.NotNull(p);
            Assert.Equal($"product_{i}", p!.Id);
        }

        // 8. Warm restart
        await manager.DisposeAsync();
        SingletonReset.Reset();

        int secondRunSupplierCalls = 0;
        var manager2 = MmapCacheManager.Initialize(tmp.Path);
        manager2.Register(new MmapCacheDefinition<Product>
        {
            Name = "products",
            Supplier = () =>
            {
                Interlocked.Increment(ref secondRunSupplierCalls);
                return GenerateProducts(ProductCount);
            },
            Serializer = p => JsonSerializer.SerializeToUtf8Bytes(p),
            Deserializer = b => JsonSerializer.Deserialize<Product>(b)!,
            Ttl = TimeSpan.FromMinutes(5),
        });

        output.WriteLine($"Warm restart: supplier calls={secondRunSupplierCalls}");
        Assert.Equal(0, secondRunSupplierCalls); // Diske yazılan WAL ve SSTable'dan okudu

        var p2 = manager2.Get<Product>("products", "product_42");
        Assert.NotNull(p2);
        
        await manager2.DisposeAsync();
        SingletonReset.Reset();
    }

    [Fact]
    public async Task Ttl_LazyExpiration_RemovesKeys()
    {
        using var tmp = new TempCacheDir();
        var mgr = MmapCacheManager.Initialize(tmp.Path);

        // Boş bir cache register et
        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "ttl-test",
            Supplier = Enumerable.Empty<(string, Widget)>,
            Serializer = w => JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => JsonSerializer.Deserialize<Widget>(b)!,
            Ttl = TimeSpan.FromHours(1) // Default TTL
        });

        // Test datası ekle, TTL'i 50ms olarak özel ver
        mgr.Put("ttl-test", "k1", new Widget("k1", "L1", 10m, 1), TimeSpan.FromMilliseconds(50));
        mgr.Put("ttl-test", "k2", new Widget("k2", "L2", 20m, 2), TimeSpan.FromMinutes(10));

        // Anında erişimde datalar gelmeli
        Assert.True(mgr.Exists("ttl-test", "k1"));
        Assert.True(mgr.Exists("ttl-test", "k2"));

        // 100ms bekle (k1'in süresi dolsun)
        await Task.Delay(100);

        // k1 süresi dolduğu için Exists false dönmeli ve Lazy silinmeli
        Assert.False(mgr.Exists("ttl-test", "k1"));
        Assert.Null(mgr.Get<Widget>("ttl-test", "k1"));

        // k2 hala yerinde olmalı
        Assert.True(mgr.Exists("ttl-test", "k2"));
        Assert.NotNull(mgr.Get<Widget>("ttl-test", "k2"));
    }
    // ── Performance Benchmark ─────────────────────────────────────────────────

    [Fact]
    public async Task PerformanceBenchmark_Measurements()
    {
        await RunBenchmarkForSize(100_000);
    }

    [Fact]
    public async Task StressTest_Scale_Measurements()
    {
        // 10M can take a while and ~1GB disk space, making it a perfect stress test
        int[] sizes = [10_000, 100_000, 1_000_000 /*, 10_000_000*/];
        
        foreach (var size in sizes)
        {
            output.WriteLine("\n*************************************************");
            output.WriteLine($"*** STRESS TEST PIPELINE: {size:N0} RECORDS ***");
            output.WriteLine("*************************************************");
            await RunBenchmarkForSize(size);
        }
    }

    private async Task RunBenchmarkForSize(int recordCount)
    {
        using var tmp = new TempCacheDir();
        var manager = MmapCacheManager.Initialize(tmp.Path);
        
        manager.Register(new MmapCacheDefinition<Product>
        {
            Name = "benchmark",
            Supplier = () => Enumerable.Empty<(string, Product)>(),
            Serializer = p => JsonSerializer.SerializeToUtf8Bytes(p),
            Deserializer = b => JsonSerializer.Deserialize<Product>(b)!
        });

        void PrintDetailedHeapInfo(string phaseName, long baselineAllocations, int baseGen0, int baseGen1, int baseGen2)
        {
            var gcInfo = GC.GetGCMemoryInfo();
            long processRam = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            long currentAllocated = GC.GetTotalAllocatedBytes();
            
            output.WriteLine($"\n--- [{phaseName}] DETAILED HEAP METRICS ---");
            output.WriteLine($"Total OS RAM (Working Set): {processRam / (1024.0 * 1024.0):F2} MB");
            output.WriteLine($"Target Heap Size          : {gcInfo.HeapSizeBytes / (1024.0 * 1024.0):F2} MB");
            output.WriteLine($"Fragmentation             : {gcInfo.FragmentedBytes / (1024.0 * 1024.0):F2} MB");
            output.WriteLine($"Net Allocated Memory      : {(currentAllocated - baselineAllocations) / (1024.0 * 1024.0):F2} MB (Total allocated volume)");
            
            output.WriteLine($"GC Collection Counts      : Gen0: {GC.CollectionCount(0) - baseGen0}, Gen1: {GC.CollectionCount(1) - baseGen1}, Gen2: {GC.CollectionCount(2) - baseGen2}");
            
            output.WriteLine("Generation Sizes:");
            output.WriteLine($"  -> Gen 0 (Short-lived)  : {gcInfo.GenerationInfo[0].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
            output.WriteLine($"  -> Gen 1 (Mid-lived)    : {gcInfo.GenerationInfo[1].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
            output.WriteLine($"  -> Gen 2 (Long-lived)   : {gcInfo.GenerationInfo[2].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
            output.WriteLine($"  -> LOH (Large Objects)  : {gcInfo.GenerationInfo[3].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
            if (gcInfo.GenerationInfo.Length > 4)
                output.WriteLine($"  -> POH (Pinned Objects) : {gcInfo.GenerationInfo[4].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
            output.WriteLine("-------------------------------------------------");
        }

        // Warmup and baseline values
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long startAllocations = GC.GetTotalAllocatedBytes();
        int baseGen0 = GC.CollectionCount(0), baseGen1 = GC.CollectionCount(1), baseGen2 = GC.CollectionCount(2);

        output.WriteLine("=================================================");
        output.WriteLine($"=== LSM ENGINE PERFORMANCE & HEAP BENCHMARK ===\nRecords: {recordCount:N0}\n");

        PrintDetailedHeapInfo("BASELINE (EMPTY STATE)", startAllocations, baseGen0, baseGen1, baseGen2);

        // ── WRITE PHASE (MemTable & WAL) ──────────────────────────────────────
        long writeStartAllocations = GC.GetTotalAllocatedBytes();
        int writeGen0 = GC.CollectionCount(0), writeGen1 = GC.CollectionCount(1), writeGen2 = GC.CollectionCount(2);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < recordCount; i++)
        {
            manager.Put("benchmark", $"key_{i}", new Product(
                Id: $"key_{i}", 
                Name: $"Benchmark Product {i}", 
                Category: "Perf", 
                Price: 10.99m, 
                Stock: 100));
        }
        sw.Stop();
        
        output.WriteLine($"[1] WRITE PHASE (MemTable & WAL)");
        output.WriteLine($"Duration   : {sw.ElapsedMilliseconds} ms");
        output.WriteLine($"Throughput : {recordCount / sw.Elapsed.TotalSeconds:N0} ops/sec");
        
        PrintDetailedHeapInfo("POST-WRITE", writeStartAllocations, writeGen0, writeGen1, writeGen2);
        
        // ── READ PHASE (Zero-Copy / MemTable Hit) ─────────────────────────────
        long readStartAllocations = GC.GetTotalAllocatedBytes();
        int readGen0 = GC.CollectionCount(0), readGen1 = GC.CollectionCount(1), readGen2 = GC.CollectionCount(2);

        sw.Restart();
        for (int i = 0; i < recordCount; i++)
        {
            var p = manager.Get<Product>("benchmark", $"key_{i}");
            Assert.NotNull(p);
        }
        sw.Stop();
        
        output.WriteLine($"\n[2] READ PHASE (Zero-Copy or MemTable Hit)");
        output.WriteLine($"Duration   : {sw.ElapsedMilliseconds} ms");
        output.WriteLine($"Throughput : {recordCount / sw.Elapsed.TotalSeconds:N0} ops/sec");
        
        PrintDetailedHeapInfo("POST-READ", readStartAllocations, readGen0, readGen1, readGen2);
        
        // ── DISK FOOTPRINT ────────────────────────────────────────────────────
        long diskBytes = Directory.GetFiles(tmp.Path, "*.*", SearchOption.AllDirectories)
                                  .Sum(f => new FileInfo(f).Length);
        
        output.WriteLine($"[3] DISK FOOTPRINT (WAL & SSTables)");
        output.WriteLine($"Total Disk Usage: {diskBytes / 1024.0 / 1024.0:F2} MB");
        output.WriteLine("=================================================\n");
        
        await manager.DisposeAsync();
        SingletonReset.Reset();
    }
}
