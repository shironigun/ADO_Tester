using System.Text.Json;
using System.Threading.Channels;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Api.Streaming;

/// <summary>
/// Server-Sent Events for the live agent console (spec §D4). Subscribes to the EventBus for a session
/// key, replays missed events (Last-Event-ID), streams <c>id:</c>/<c>data:</c> frames, sends a
/// <c>: keepalive</c> comment every 15s so proxies don't drop the connection, and ends on a terminal
/// event or client disconnect. The buffering-defeating headers (<c>X-Accel-Buffering: no</c>,
/// <c>Cache-Control: no-transform</c>) are mandatory or App Service/Front Door silently buffers the stream.
/// </summary>
public static class SseWriter
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    /// <summary>Last-Event-ID resolution: header first, then query; digit-strings only (else null).</summary>
    public static long? ParseLastEventId(string? header, string? query)
    {
        foreach (var v in new[] { header, query })
            if (!string.IsNullOrEmpty(v) && v.All(char.IsDigit) && long.TryParse(v, out var n))
                return n;
        return null;
    }

    public static async Task StreamAsync(HttpResponse response, IEventBus bus, string key, long? afterSeq, CancellationToken cancellationToken)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers.Connection = "keep-alive";

        using var subscription = bus.Subscribe(key, afterSeq);
        var reader = subscription.Reader;
        try
        {
            await response.WriteAsync(": connected\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);

            while (true)
            {
                var readTask = reader.ReadAsync(cancellationToken).AsTask();
                var done = await Task.WhenAny(readTask, Task.Delay(Heartbeat, cancellationToken));
                if (done != readTask)
                {
                    await response.WriteAsync(": keepalive\n\n", cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                    continue;
                }

                var ev = await readTask;
                var json = JsonSerializer.Serialize(ev.ToCamel());
                await response.WriteAsync($"id: {ev.Seq}\ndata: {json}\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
                if (ev.IsTerminal)
                    break;
            }
        }
        catch (OperationCanceledException) { /* client disconnected — normal */ }
        catch (ChannelClosedException) { /* subscription completed */ }
    }
}
