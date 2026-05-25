namespace MmapCache.Cache;

public interface ICacheDefinition
{
    TimeSpan Ttl { get; }
}
