using Microsoft.Extensions.Caching.Memory;

namespace Campfire.Features.Identity;

// Explicit development/test fallback; production always uses the shared Redis counter.
internal sealed class LocalLoginLimiter : IDisposable
{
    private readonly MemoryCache counters = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly object gate = new();
    private sealed class Counter { public long Count; }
    public long Increment(string ip)
    {
        lock (gate)
        {
            if (!counters.TryGetValue(ip, out Counter? counter))
                counters.Set(ip, counter = new Counter(), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(3), Size = 1 });
            return ++counter!.Count;
        }
    }
    public void Dispose() => counters.Dispose();
}
