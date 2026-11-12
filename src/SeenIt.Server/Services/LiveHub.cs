using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Tells signed-in pages that something of theirs changed, so they reload instead of polling: a check-in
/// starting or finishing (Jellyfin, Plex, Kodi or a manual one running out), a play recorded by a webhook,
/// or any change made from another device. Each open page holds one GET /api/live (server-sent events).
/// The message is the id of the client that made the change (empty for the server), so that client can skip it.
/// </summary>
public class LiveHub
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<string>>> subscribers = new();

    public (ChannelReader<string> Reader, IDisposable Subscription) Subscribe(Guid userId)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
        subscribers.GetOrAdd(userId, _ => new()).TryAdd(id, channel);
        return (channel.Reader, new Unsubscribe(() =>
        {
            if (subscribers.TryGetValue(userId, out var mine))
            {
                mine.TryRemove(id, out _);
                if (mine.IsEmpty) subscribers.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<Guid, Channel<string>>>(userId, mine));
            }
        }));
    }

    public void Notify(Guid userId, string? fromClient = null)
    {
        if (!subscribers.TryGetValue(userId, out var mine)) return;
        foreach (var c in mine.Values) c.Writer.TryWrite(fromClient ?? "");
    }

    /// <summary>The user and the people who follow them (their Home shows what friends are watching).</summary>
    public async Task NotifyWithFollowersAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        Notify(userId);
        if (subscribers.Keys.All(k => k == userId)) return;   // nobody else is listening
        var listening = subscribers.Keys.ToList();
        var followers = await db.Follows.Where(f => f.FolloweeId == userId && listening.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct);
        foreach (var f in followers) Notify(f);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    public const string ClientHeader = "X-SeenIt-Client";

    /// <summary>After a signed-in change (any non-GET API call that worked), tell that user's other pages.</summary>
    public static async Task Middleware(HttpContext ctx, Func<Task> next)
    {
        await next();
        var path = ctx.Request.Path;
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method)) return;
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/catalog")) return;
        if (ctx.Response.StatusCode >= 400) return;
        if (ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { } sub || !Guid.TryParse(sub, out var userId)) return;
        ctx.RequestServices.GetRequiredService<LiveHub>().Notify(userId, ctx.Request.Headers[ClientHeader].FirstOrDefault());
    }

    /// <summary>GET /api/live: an event-stream that stays open. A comment line every 25s keeps proxies from closing it.</summary>
    public static async Task StreamAsync(HttpContext http, Guid userId, LiveHub hub, CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var (reader, subscription) = hub.Subscribe(userId);
        using var _ = subscription;
        try
        {
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                string? message = null;
                using (var beat = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    beat.CancelAfter(TimeSpan.FromSeconds(25));
                    try
                    {
                        if (await reader.WaitToReadAsync(beat.Token)) reader.TryRead(out message);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
                await http.Response.WriteAsync(message is null ? ": ping\n\n" : $"data: {message}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }   // the page went away
    }
}
