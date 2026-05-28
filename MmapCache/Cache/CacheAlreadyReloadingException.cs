namespace MmapCache.Cache;

/// <summary>
/// Thrown when a cache reload operation is already in progress.
/// </summary>
public sealed class CacheAlreadyReloadingException : InvalidOperationException
{
    /// <summary>
    /// Thrown when a cache reload operation is already in progress.
    /// </summary>
    public CacheAlreadyReloadingException()
        : base("The cache is already being reloaded.")
    {
    }

    /// <summary>
    /// Thrown when a cache reload operation is already in progress.
    /// </summary>
    public CacheAlreadyReloadingException(string? message)
        : base(message)
    {
    }

    /// <summary>
    /// Thrown when a cache reload operation is already in progress.
    /// </summary>
    public CacheAlreadyReloadingException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
