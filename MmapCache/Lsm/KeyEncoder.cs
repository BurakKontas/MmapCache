using System;
using System.Text;

namespace MmapCache.Lsm;

public static class KeyEncoder
{
    public static byte[] ToUtf8(string key)
        => Encoding.UTF8.GetBytes(key);
    public static int GetBytes(string key, Span<byte> destination)
        => Encoding.UTF8.GetBytes(key, destination);

    public static ReadOnlySpan<byte> AsSpan(string key)
        => Encoding.UTF8.GetBytes(key);
}