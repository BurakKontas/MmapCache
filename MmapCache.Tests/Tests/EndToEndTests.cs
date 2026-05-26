using MmapCache.Cache;
using MmapCache.Config;
using MmapCache.Tests.Helpers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit.Abstractions;

namespace MmapCache.Tests.Tests;

[Collection("Sequential")]
public sealed class EndToEndTests
{
    private readonly ITestOutputHelper _output;
    private readonly bool _isDebugMode;

    public EndToEndTests(ITestOutputHelper output)
    {
        _output = output;

#if DEBUG
        _isDebugMode = true;
#else
        _isDebugMode = false;
#endif
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

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

    /// <summary>Log written always (both Debug and Release)</summary>
    private void Log(string message)
    {
        _output.WriteLine(message);
        Console.WriteLine(message);
    }

    /// <summary>Progress log written only in Debug mode</summary>
    private void LogProgress(string message)
    {
        if (_isDebugMode)
        {
            _output.WriteLine(message);
            Console.WriteLine(message);
        }
    }

    [Fact]
    public async Task FullScenario_LoadReadReloadConcurrent()
    {
        using var tmp = new TempCacheDir();
        const int ProductCount = 5_000;

        var manager = MmapCacheManager.Initialize(tmp.Path);
        manager.Register(ProductDef(ProductCount));

        Thread.Sleep(100);

        Log($"Loaded — size={manager.Size("products"):N0}  ts={manager.LastReload("products"):HH:mm:ss}");
        Assert.Equal(ProductCount, manager.Size("products"));

        string[] testKeys = ["product_0", "product_42", $"product_{ProductCount - 1}", "product_not_exist"];
        foreach (var key in testKeys)
        {
            var p = manager.Get<Product>("products", key);
            if (key == "product_not_exist")
                Assert.Null(p);
            else
                Assert.NotNull(p);
        }

        Assert.True(manager.Exists("products", "product_1000"));
        Assert.False(manager.Exists("products", "product_xyz"));

        const int ReadCount = 5_000;
        var rng = new Random(42);
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < ReadCount; i++)
            manager.Get<Product>("products", $"product_{rng.Next(ProductCount)}");

        sw.Stop();
        double opsPerSec = ReadCount / sw.Elapsed.TotalSeconds;
        Log($"Reads: {ReadCount:N0} in {sw.ElapsedMilliseconds}ms → {opsPerSec:N0} ops/s");

        bool reading = true;
        int readsDone = 0;
        int nullsReturned = 0;

        var bgReads = Task.Run(() =>
        {
            var r = new Random(99);
            while (Volatile.Read(ref reading))
            {
                var result = manager.Get<Product>("products", $"product_{r.Next(ProductCount)}");
                if (result is null) Interlocked.Increment(ref nullsReturned);
                Interlocked.Increment(ref readsDone);
            }
        });

        await manager.ReloadAsync<Product>("products");

        Volatile.Write(ref reading, false);
        await bgReads;

        Log($"Reads during reload: {readsDone:N0}");
        Assert.True(readsDone > 0, "Background reader did not execute.");

        for (int i = 0; i < Math.Min(100, ProductCount); i++)
        {
            var p = manager.Get<Product>("products", $"product_{i}");
            Assert.NotNull(p);
            Assert.Equal($"product_{i}", p!.Id);
        }

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

        Log($"Warm restart: supplier calls={secondRunSupplierCalls}");
        Assert.Equal(0, secondRunSupplierCalls);

        var p2 = manager2.Get<Product>("products", "product_42");
        Assert.NotNull(p2);

        await manager2.DisposeAsync();
        SingletonReset.Reset();
    }

    [Fact]
    public async Task Ttl_LazyExpiration_RemovesKeys()
    {
        using var tmp = new TempCacheDir();

        await using var mgr = MmapCacheManager.Initialize(tmp.Path);

        mgr.Register(new MmapCacheDefinition<Widget>
        {
            Name = "ttl-test",
            Supplier = Enumerable.Empty<(string, Widget)>,
            Serializer = w => JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => JsonSerializer.Deserialize<Widget>(b)!,
            Ttl = TimeSpan.FromHours(1)
        });

        mgr.Put(
            "ttl-test",
            "k1",
            new Widget("k1", "L1", 10m, 1),
            TimeSpan.FromMilliseconds(50));

        mgr.Put(
            "ttl-test",
            "k2",
            new Widget("k2", "L2", 20m, 2),
            TimeSpan.FromMinutes(10));

        Assert.True(mgr.Exists("ttl-test", "k1"));
        Assert.True(mgr.Exists("ttl-test", "k2"));

        await Task.Delay(100);

        Assert.False(mgr.Exists("ttl-test", "k1"));
        Assert.Null(mgr.Get<Widget>("ttl-test", "k1"));

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
        // radixCapacity = number of trie NODEs (not number of keys).
        // For key_N format: 4 fixed nodes ("key_") + 10^i nodes for each digit level.
        // Formula: sum(10^i, i=1..digits) + 4. ALWAYS requires MORE nodes than keys.
        var configs = new Dictionary<int, (int radixCapacity, int flushThresholdMB)>
        {
            { 1_000,       (2_000,       64) },  // needed ~1_114
            { 10_000,      (15_000,      64) },  // needed ~11_114
            { 100_000,     (150_000,     32) },  // needed ~111_114
            { 1_000_000,   (1_500_000,   32) },  // needed ~1_111_114
            { 5_000_000,   (7_000_000,   16) },  // needed ~6_111_114
            { 10_000_000,  (12_500_000,  16) },  // needed ~11_111_114
            { 50_000_000,  (65_000_000,   4) },  // needed ~61_111_114 
            { 100_000_000, (115_000_000,  2) },  // needed ~111_111_114
        };

        //int[] sizes = [1_000, 10_000, 100_000, 1_000_000, 5_000_000, 10_000_000, 50_000_000, 100_000_000];
        int[] sizes = [1_000];

        foreach (var size in sizes)
        {
            Log("\n*************************************************");
            Log($"*** STRESS TEST PIPELINE: {size:N0} RECORDS ***");
            Log("*************************************************");

            try
            {
                if (configs.TryGetValue(size, out var config))
                {
                    await RunBenchmarkForSize(
                        recordCount: size,
                        radixTreeCapacity: config.radixCapacity,
                        flushThresholdBytes: config.flushThresholdMB * 1024 * 1024
                    );
                }
                else
                {
                    await RunBenchmarkForSize(recordCount: size);
                }

                Log($"\nCompleted {size:N0} records test successfully\n");
            }
            finally
            {
                Log($"🧹 Cleaning up memory after {size:N0} test...");

                ForceGC();

                if (size >= 1_000_000)
                {
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);

                    Thread.Sleep(2000);

                    ForceGC();

                    long processRam = Process.GetCurrentProcess().WorkingSet64;
                    Log($"🧹 After cleanup - Working Set: {processRam / (1024.0 * 1024.0):F2} MB");
                }
            }
        }
    }

    private async Task RunBenchmarkForSize(
        int recordCount,
        int? radixTreeCapacity = null,
        int? flushThresholdBytes = null)
    {
        try
        {
            using var tmp = new TempCacheDir();
            var manager = MmapCacheManager.Initialize(tmp.Path);

            int treeCapacity = radixTreeCapacity ?? recordCount + 1000;
            int flushBytes = flushThresholdBytes ?? 64 * 1024 * 1024;

            Log($"⚙️ Configuration: RadixTreeCapacity={treeCapacity:N0}, FlushThreshold={flushBytes / (1024 * 1024)}MB");

            manager.Register(new MmapCacheDefinition<Product>
            {
                Name = "benchmark",
                Supplier = () => Enumerable.Empty<(string, Product)>(),
                Serializer = p => JsonSerializer.SerializeToUtf8Bytes(p),
                Deserializer = b => JsonSerializer.Deserialize<Product>(b)!,
                RadixTreeCapacity = treeCapacity,
                MemTableFlushThresholdBytes = flushBytes
            });

            void PrintDetailedHeapInfo(string phaseName, long baselineAllocations, int baseGen0, int baseGen1, int baseGen2)
            {
                var gcInfo = GC.GetGCMemoryInfo();
                long processRam = Process.GetCurrentProcess().WorkingSet64;
                long currentAllocated = GC.GetTotalAllocatedBytes();

                Log($"\n--- [{phaseName}] DETAILED HEAP METRICS ---");
                Log($"Total OS RAM (Working Set): {processRam / (1024.0 * 1024.0):F2} MB");
                Log($"Target Heap Size          : {gcInfo.HeapSizeBytes / (1024.0 * 1024.0):F2} MB");
                Log($"Fragmentation             : {gcInfo.FragmentedBytes / (1024.0 * 1024.0):F2} MB");
                Log($"Net Allocated Memory      : {(currentAllocated - baselineAllocations) / (1024.0 * 1024.0):F2} MB (Total allocated volume)");
                Log($"GC Collection Counts      : Gen0: {GC.CollectionCount(0) - baseGen0}, Gen1: {GC.CollectionCount(1) - baseGen1}, Gen2: {GC.CollectionCount(2) - baseGen2}");
                Log("Generation Sizes:");
                Log($"  -> Gen 0 (Short-lived)  : {gcInfo.GenerationInfo[0].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
                Log($"  -> Gen 1 (Mid-lived)    : {gcInfo.GenerationInfo[1].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
                Log($"  -> Gen 2 (Long-lived)   : {gcInfo.GenerationInfo[2].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
                Log($"  -> LOH (Large Objects)  : {gcInfo.GenerationInfo[3].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
                if (gcInfo.GenerationInfo.Length > 4)
                    Log($"  -> POH (Pinned Objects) : {gcInfo.GenerationInfo[4].SizeAfterBytes / (1024.0 * 1024.0):F2} MB");
                Log("-------------------------------------------------");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long startAllocations = GC.GetTotalAllocatedBytes();
            int baseGen0 = GC.CollectionCount(0), baseGen1 = GC.CollectionCount(1), baseGen2 = GC.CollectionCount(2);

            Log("=================================================");
            Log($"=== LSM ENGINE PERFORMANCE & HEAP BENCHMARK ===\nRecords: {recordCount:N0}\n");

            PrintDetailedHeapInfo("BASELINE (EMPTY STATE)", startAllocations, baseGen0, baseGen1, baseGen2);

            // ── WRITE PHASE (CHUNKED) ─────────────────────────────────────────
            long writeStartAllocations = GC.GetTotalAllocatedBytes();
            int writeGen0 = GC.CollectionCount(0), writeGen1 = GC.CollectionCount(1), writeGen2 = GC.CollectionCount(2);

            var sw = Stopwatch.StartNew();
            int batchSize = Math.Min(10_000, Math.Max(1_000, recordCount / 10));

            if (recordCount >= 50_000_000)
            {
                int superChunkSize = 5_000_000;
                int superTotalChunks = (int)Math.Ceiling((double)recordCount / superChunkSize);

                Log($"📦 Super Chunked Write Mode: {superTotalChunks} super chunks of {superChunkSize:N0} records each");

                for (int superChunk = 0; superChunk < superTotalChunks; superChunk++)
                {
                    int superStart = superChunk * superChunkSize;
                    int superEnd = Math.Min(superStart + superChunkSize, recordCount);

                    Log($"  📝 Super Chunk {superChunk + 1}/{superTotalChunks} ({superStart:N0} → {superEnd - 1:N0})");
                    Log($"  📊 Current RAM before super chunk: {Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0):F2} MB");

                    int miniChunkSize = 500_000;
                    int miniTotalChunks = (int)Math.Ceiling((double)(superEnd - superStart) / miniChunkSize);

                    for (int miniChunk = 0; miniChunk < miniTotalChunks; miniChunk++)
                    {
                        int start = superStart + (miniChunk * miniChunkSize);
                        int end = Math.Min(start + miniChunkSize, superEnd);

                        for (int i = start; i < end; i++)
                        {
                            manager.Put("benchmark", $"key_{i}", new Product(
                                Id: $"key_{i}",
                                Name: $"Benchmark Product {i}",
                                Category: "Perf",
                                Price: 10.99m,
                                Stock: 100));

                            if ((i - start + 1) % batchSize == 0)
                            {
                                var elapsed = sw.ElapsedMilliseconds;
                                var currentOps = (i + 1) / (elapsed / 1000.0);
                                LogProgress($"    -> Mini Chunk Progress: {i - start + 1:N0}/{end - start:N0} ({currentOps:N0} ops/sec)");
                            }
                        }

                        ForceGC();
                        await Task.Delay(500);
                    }

                    Log($"  🔄 Super Chunk {superChunk + 1}/{superTotalChunks} completed. Deep cleaning...");

                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);

                    await Task.Delay(3000);

                    ForceGC();

                    long currentRam = Process.GetCurrentProcess().WorkingSet64;
                    Log($"  📊 RAM after super chunk {superChunk + 1}: {currentRam / (1024.0 * 1024.0):F2} MB");
                }
            }
            else if (recordCount >= 5_000_000)
            {
                int chunkSize = 500_000;
                int totalChunks = (int)Math.Ceiling((double)recordCount / chunkSize);

                Log($"📦 Chunked Write Mode: {totalChunks} chunks of {chunkSize:N0} records each");

                for (int chunk = 0; chunk < totalChunks; chunk++)
                {
                    int start = chunk * chunkSize;
                    int end = Math.Min(start + chunkSize, recordCount);
                    int chunkRecordCount = end - start;

                    LogProgress($"  📝 Writing Chunk {chunk + 1}/{totalChunks} ({start:N0} → {end - 1:N0})...");

                    for (int i = start; i < end; i++)
                    {
                        manager.Put("benchmark", $"key_{i}", new Product(
                            Id: $"key_{i}",
                            Name: $"Benchmark Product {i}",
                            Category: "Perf",
                            Price: 10.99m,
                            Stock: 100));

                        if ((i - start + 1) % batchSize == 0)
                        {
                            var elapsed = sw.ElapsedMilliseconds;
                            var currentOps = (i + 1) / (elapsed / 1000.0);
                            LogProgress($"    -> Chunk Progress: {i - start + 1:N0}/{chunkRecordCount:N0} ({currentOps:N0} ops/sec)");
                        }
                    }

                    LogProgress($"  🔄 Chunk {chunk + 1}/{totalChunks} completed. GC + Flush wait...");
                    ForceGC();
                    await Task.Delay(1000);
                }
            }
            else
            {
                for (int i = 0; i < recordCount; i++)
                {
                    manager.Put("benchmark", $"key_{i}", new Product(
                        Id: $"key_{i}",
                        Name: $"Benchmark Product {i}",
                        Category: "Perf",
                        Price: 10.99m,
                        Stock: 100));

                    if ((i + 1) % batchSize == 0)
                    {
                        var elapsed = sw.ElapsedMilliseconds;
                        var currentOps = (i + 1) / (elapsed / 1000.0);
                        LogProgress($"  -> Write Progress: {i + 1:N0}/{recordCount:N0} records ({currentOps:N0} ops/sec)");
                    }
                }
            }
            sw.Stop();

            Log($"\n[1] WRITE PHASE (MemTable & WAL)");
            Log($"Duration   : {sw.ElapsedMilliseconds} ms");
            Log($"Throughput : {recordCount / sw.Elapsed.TotalSeconds:N0} ops/sec");

            PrintDetailedHeapInfo("POST-WRITE", writeStartAllocations, writeGen0, writeGen1, writeGen2);

            // ── READ PHASE (CHUNKED) ──────────────────────────────────────────

            // Allocate array *before* capturing baseline memory to keep metrics accurate. 
            // Warning: For 100M records, this array is ~800MB allocated on the Large Object Heap (LOH).
            long[] readLatenciesTicks = new long[recordCount];

            long readStartAllocations = GC.GetTotalAllocatedBytes();
            int readGen0 = GC.CollectionCount(0), readGen1 = GC.CollectionCount(1), readGen2 = GC.CollectionCount(2);

            sw.Restart();

            // CHUNKING: Segmented reading for large data sets
            if (recordCount >= 5_000_000)
            {
                int chunkSize = 500_000; // 500K chunk size
                int totalChunks = (int)Math.Ceiling((double)recordCount / chunkSize);

                Log($"📦 Chunked Read Mode: {totalChunks} chunks of {chunkSize:N0} records each");

                for (int chunk = 0; chunk < totalChunks; chunk++)
                {
                    int start = chunk * chunkSize;
                    int end = Math.Min(start + chunkSize, recordCount);
                    int chunkRecordCount = end - start;

                    LogProgress($"  📖 Reading Chunk {chunk + 1}/{totalChunks} ({start:N0} → {end - 1:N0})...");

                    for (int i = start; i < end; i++)
                    {
                        long tickStart = Stopwatch.GetTimestamp();
                        var p = manager.Get<Product>("benchmark", $"key_{i}");
                        readLatenciesTicks[i] = Stopwatch.GetTimestamp() - tickStart;

                        if (p == null)
                        {
                            Log($"  ⚠️ WARNING: Null result for key_{i} at position {i}");
                        }

                        if ((i - start + 1) % batchSize == 0)
                        {
                            var elapsed = sw.ElapsedMilliseconds;
                            var currentOps = (i - start + 1) / (elapsed / 1000.0);
                            LogProgress($"    -> Chunk Progress: {i - start + 1:N0}/{chunkRecordCount:N0} ({currentOps:N0} ops/sec)");
                        }
                    }

                    LogProgress($"  ✅ Chunk {chunk + 1}/{totalChunks} read completed.");
                    ForceGC(); // GC after each chunk
                }
            }
            else
            {
                // Normal reading for smaller data sets
                for (int i = 0; i < recordCount; i++)
                {
                    long tickStart = Stopwatch.GetTimestamp();
                    var p = manager.Get<Product>("benchmark", $"key_{i}");
                    readLatenciesTicks[i] = Stopwatch.GetTimestamp() - tickStart;

                    if (p == null)
                    {
                        Log($"  ⚠️ WARNING: Null result for key_{i} at position {i}");
                    }

                    if ((i + 1) % batchSize == 0)
                    {
                        var elapsed = sw.ElapsedMilliseconds;
                        var currentOps = (i + 1) / (elapsed / 1000.0);
                        LogProgress($"  -> Read Progress: {i + 1:N0}/{recordCount:N0} records ({currentOps:N0} ops/sec)");
                    }
                }
            }
            sw.Stop();

            Log($"\n[2] READ PHASE (Zero-Copy or MemTable Hit)");
            Log($"Duration   : {sw.ElapsedMilliseconds} ms");
            Log($"Throughput : {recordCount / sw.Elapsed.TotalSeconds:N0} ops/sec");

            // --- PERCENTILE CALCULATIONS ---
            Log("  📊 Calculating percentiles...");
            Array.Sort(readLatenciesTicks);

            double tickToUs = 1_000_000.0 / Stopwatch.Frequency;
            double sumTicks = 0;

            // Loop is much faster & lower allocation than LINQ .Average() for millions of elements
            for (int i = 0; i < recordCount; i++)
            {
                sumTicks += readLatenciesTicks[i];
            }

            double avgUs = (sumTicks / recordCount) * tickToUs;
            double p50Us = readLatenciesTicks[(int)(recordCount * 0.50)] * tickToUs;
            double p95Us = readLatenciesTicks[(int)(recordCount * 0.95)] * tickToUs;
            double p99Us = readLatenciesTicks[(int)(recordCount * 0.99)] * tickToUs;
            double p999Us = readLatenciesTicks[(int)(recordCount * 0.999)] * tickToUs;
            double maxUs = readLatenciesTicks[recordCount - 1] * tickToUs;

            Log($"  ⏱️ Latency (µs): Avg: {avgUs:F2} | p50: {p50Us:F2} | p95: {p95Us:F2} | p99: {p99Us:F2} | p99.9: {p999Us:F2} | Max: {maxUs:F2}");

            // Clear the massive array strictly BEFORE checking heap memory size so GC can reclaim it
            readLatenciesTicks = null!;

            PrintDetailedHeapInfo("POST-READ", readStartAllocations, readGen0, readGen1, readGen2);

            // ── DISK FOOTPRINT ────────────────────────────────────────────────
            long diskBytes = Directory.GetFiles(tmp.Path, "*.*", SearchOption.AllDirectories)
                                      .Sum(f => new FileInfo(f).Length);

            Log($"[3] DISK FOOTPRINT (WAL & SSTables)");
            Log($"Total Disk Usage: {diskBytes / 1024.0 / 1024.0:F2} MB");
            Log("=================================================\n");

            await manager.DisposeAsync();
            SingletonReset.Reset();
        }
        catch (OutOfMemoryException ex)
        {
            Log($"❌ OutOfMemory at {recordCount:N0} records: {ex.Message}");
            Log($"   Consider reducing FlushThreshold or increasing RadixTreeCapacity");
            throw;
        }
        catch (Exception ex)
        {
            Log($"❌ Error at {recordCount:N0} records: {ex.GetType().Name} - {ex.Message}");
            throw;
        }
        finally
        {
            ForceGC();
        }
    }

    private void ForceGC()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
    }
}