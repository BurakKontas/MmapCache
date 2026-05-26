using MmapCache.Cache;
using MmapCache.Config;
using MmapCache.Tests.Helpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;

namespace MmapCache.Tests.Tests;

[Collection("Sequential")]
public sealed class MmapCacheConcurrencyStressTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _cacheName = "race-condition-heavy-stress";

    public MmapCacheConcurrencyStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void Log(string message)
    {
        _output.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
    }

    [Fact]
    public async Task UltraHeavy_ReadWriteReload_RaceCondition_ForceTest()
    {
        using var tmp = new TempCacheDir();
        var manager = MmapCacheManager.Initialize(tmp.Path);

        const int InitialRecordCount = 20_000;
        const int ConcurrencyRuntimeSeconds = 10;

        manager.Register(new MmapCacheDefinition<Widget>
        {
            Name = _cacheName,
            Supplier = () => TestFactory.MakeWidgets(InitialRecordCount, "init_widget"),
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
            Ttl = TimeSpan.FromMinutes(30),
            RadixTreeCapacity = 2_000_000,
            MemTableFlushThresholdBytes = 2 * 1024 * 1024
        });

        Log("🔥 Concurrency stress test started.");

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(ConcurrencyRuntimeSeconds));

        long totalPuts = 0;
        long totalGets = 0;
        long totalReloads = 0;
        long failedReads = 0;

        var exceptions = new ConcurrentBag<Exception>();

        // =========================
        // WRITERS
        // =========================
        var writerTask = Task.Run(() =>
        {
            try
            {
                Parallel.For(0, 8, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = cts.Token
                },
                workerId =>
                {
                    int i = 0;

                    while (!cts.Token.IsCancellationRequested)
                    {
                        string key = $"stress_key_{workerId}_{i++ % 2000}";

                        manager.Put(_cacheName, key,
                            new Widget(key, "Live", i, i % 10));

                        Interlocked.Increment(ref totalPuts);

                        Thread.Yield(); // prevent CPU starvation
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                exceptions.Add(new Exception($"Writer Exception: {ex.Message}", ex));
            }
        }, cts.Token);

        // =========================
        // READERS
        // =========================
        var readerTask = Task.Run(() =>
        {
            try
            {
                Parallel.For(0, 12, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 6,
                    CancellationToken = cts.Token
                },
                workerId =>
                {
                    var rng = new Random(workerId);

                    while (!cts.Token.IsCancellationRequested)
                    {
                        string key = rng.Next(3) switch
                        {
                            0 => $"init_widget_{rng.Next(InitialRecordCount)}",
                            1 => $"stress_key_{rng.Next(8)}_{rng.Next(2000)}",
                            _ => $"missing_{rng.Next(10000)}"
                        };

                        try
                        {
                            var result = manager.Get<Widget>(_cacheName, key);
                            Interlocked.Increment(ref totalGets);

                            if (result != null &&
                                (string.IsNullOrEmpty(result.Id) || result.Price < 0))
                            {
                                Interlocked.Increment(ref failedReads);
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failedReads);
                            exceptions.Add(new Exception($"Reader Exception: {ex.Message}", ex));
                        }

                        Thread.SpinWait(40);
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                exceptions.Add(new Exception($"Reader Infra Exception: {ex.Message}", ex));
            }
        }, cts.Token);

        // =========================
        // RELOADER / BURST ACTOR
        // =========================
        var reloaderTask = Task.Run(async () =>
        {
            int cycle = 0;

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Random.Shared.Next(500, 900), cts.Token);

                    if (cycle++ % 4 == 0)
                    {
                        Log("🔄 ReloadAsync triggered");
                        await manager.ReloadAsync<Widget>(_cacheName, cts.Token);
                    }
                    else
                    {
                        Log("🧹 Burst writes");

                        for (int i = 0; i < 300; i++)
                        {
                            manager.Put(_cacheName,
                                $"burst_{i}",
                                new Widget($"burst_{i}", "Burst", i, i));
                        }
                    }

                    Interlocked.Increment(ref totalReloads);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    exceptions.Add(new Exception($"Reloader Exception: {ex.Message}", ex));
                }
            }
        }, cts.Token);

        await Task.WhenAll(writerTask, readerTask, reloaderTask);

        Log("🏁 Test finished");

        Log($"Puts: {totalPuts:N0}");
        Log($"Gets: {totalGets:N0}");
        Log($"Reloads: {totalReloads:N0}");
        Log($"Failed reads: {failedReads:N0}");

        if (!exceptions.IsEmpty)
        {
            Log($"❌ Errors: {exceptions.Count}");

            foreach (var ex in exceptions.Take(10))
                Log(ex.Message);

            throw new AggregateException(exceptions);
        }

        Assert.True(totalPuts > 0);
        Assert.True(totalGets > 0);
        Assert.Equal(0, failedReads);

        Log("✅ SUCCESS");

        await manager.DisposeAsync();
        SingletonReset.Reset();
    }

    public void Dispose()
    {
        SingletonReset.Reset();
    }
}