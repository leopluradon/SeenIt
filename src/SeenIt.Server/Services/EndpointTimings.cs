using System.Collections.Concurrent;
using System.Diagnostics;
using SeenIt.Contracts;

namespace SeenIt.Server.Services;

/// <summary>How long each API endpoint takes, since the last restart, for the admin page.</summary>
public sealed class EndpointTimings
{
    private const int Samples = 200;   // recent requests kept per endpoint, for the 95th percentile
    private readonly ConcurrentDictionary<string, Stat> _stats = new();

    private sealed class Stat
    {
        public long Count;
        public double TotalMs, MaxMs;
        public readonly Queue<double> Recent = new();
    }

    public void Record(string route, double ms)
    {
        var s = _stats.GetOrAdd(route, _ => new Stat());
        lock (s)
        {
            s.Count++;
            s.TotalMs += ms;
            s.MaxMs = Math.Max(s.MaxMs, ms);
            s.Recent.Enqueue(ms);
            if (s.Recent.Count > Samples) s.Recent.Dequeue();
        }
    }

    public List<AdminEndpointTiming> Snapshot() => _stats.Select(kv =>
    {
        lock (kv.Value)
        {
            var sorted = kv.Value.Recent.Order().ToList();
            var p95 = sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * .95) - 1)];
            return new AdminEndpointTiming(kv.Key, kv.Value.Count, Math.Round(kv.Value.TotalMs / Math.Max(1, kv.Value.Count)), Math.Round(p95), Math.Round(kv.Value.MaxMs));
        }
    }).OrderByDescending(t => t.P95Ms).ToList();

    /// <summary>Times API requests by route pattern ("GET /api/catalog/movie/{id:int}"), not by URL.</summary>
    public static async Task Middleware(HttpContext ctx, Func<Task> next)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(); return; }
        var started = Stopwatch.GetTimestamp();
        try { await next(); }
        finally
        {
            if (ctx.GetEndpoint() is RouteEndpoint e)
                ctx.RequestServices.GetRequiredService<EndpointTimings>()
                    .Record($"{ctx.Request.Method} /{e.RoutePattern.RawText?.TrimStart('/')}", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
