using System.Reflection;
using MmapCache.Cache;

namespace MmapCache.Tests.Helpers;

/// <summary>
/// Resets the MmapCacheManager singleton between tests.
///
/// MmapCacheManager._instance is a private static field; we reach it via
/// reflection so each test can call Initialize() with a fresh state.
/// </summary>
public static class SingletonReset
{
    private static readonly FieldInfo InstanceField =
        typeof(MmapCacheManager)
            .GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not locate MmapCacheManager._instance field.");

    public static void Reset() => InstanceField.SetValue(null, null);

    public static MmapCacheManager? GetCurrent()
        => (MmapCacheManager?)InstanceField.GetValue(null);
}

/// <summary>
/// Creates a throw-away temp directory for a single test and cleans it up
/// when the test finishes.  Also resets the singleton before and after.
/// </summary>
public sealed class TempCacheDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mmap-test-{Guid.NewGuid():N}");

    public TempCacheDir()
    {
        // Make sure there is no leftover instance from a previous (failed) test.
        DisposeCurrentManager();
        SingletonReset.Reset();
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        DisposeCurrentManager();
        SingletonReset.Reset();
        try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
    }

    private static void DisposeCurrentManager()
    {
        try
        {
            var current = SingletonReset.GetCurrent();
            current?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }
    }
}

/// <summary>
/// Canonical test value type used across all test classes.
/// </summary>
public sealed record Widget(string Id, string Label, decimal Price, int Stock);

/// <summary>
/// Helpers shared by multiple test classes.
/// </summary>
public static class TestFactory
{
    // ── Widget helpers ────────────────────────────────────────────────────────

    public static IEnumerable<(string, Widget)> MakeWidgets(int count, string? prefix = null)
    {
        prefix ??= "widget";
        for (int i = 0; i < count; i++)
        {
            var w = new Widget($"{prefix}_{i}", $"Label #{i}", i * 1.5m, i % 100);
            yield return ($"{prefix}_{i}", w);
        }
    }

    public static MmapCache.Config.MmapCacheDefinition<Widget> WidgetDef(
        string name,
        int count,
        string? supplierPrefix = null,
        TimeSpan? ttl = null,
        int l1MaxSize = 1_000,
        TimeSpan? l1Ttl = null,
        bool dynamicSizing = true) =>
        new()
        {
            Name = name,
            Supplier = () => MakeWidgets(count, supplierPrefix ?? name.Replace("-", "_")),
            Serializer = w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w),
            Deserializer = b => System.Text.Json.JsonSerializer.Deserialize<Widget>(b)!,
            Ttl = ttl ?? TimeSpan.FromHours(1),
            DynamicSizing = dynamicSizing,
            L1MaxSize = l1MaxSize,
            L1Ttl = l1Ttl,
        };
}