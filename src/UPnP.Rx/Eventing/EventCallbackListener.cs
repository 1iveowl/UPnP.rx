using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using SimpleHttpListener.Rx;
using SimpleHttpListener.Rx.Model;

namespace UPnP.Rx.Eventing;

/// <summary>One incoming NOTIFY, reduced to what the subscription engine needs.</summary>
/// <param name="Sid">The subscription identifier the device addressed (<c>SID</c> header).</param>
/// <param name="Seq">The event key (<c>SEQ</c>), when the device sent a usable one.</param>
/// <param name="Body">The property-set body as text.</param>
internal sealed record NotifyRequest(string? Sid, uint? Seq, string Body);

/// <summary>
/// The shared inbound half of eventing: routes NOTIFY requests to registered
/// subscriptions by callback-path token and answers them on the wire
/// (200 for handled, 412 for unknown - UDA 2.0 clause 4.3.3). Sits behind an
/// injectable request stream + response sender, so tests drive it from a
/// Subject without sockets (the HotStart discipline, applied to eventing).
/// </summary>
internal sealed class EventCallbackListener : IDisposable
{
    /// <summary>Sends an HTTP response for a received request (production: <c>HttpSender.SendResponseAsync</c>).</summary>
    internal delegate Task ResponseSender(HttpRequestResponse request, HttpResponse response, CancellationToken ct);

    private readonly ConcurrentDictionary<string, Func<NotifyRequest, CancellationToken, Task>> _routes = new();
    private readonly IDisposable _subscription;
    private readonly TcpListener? _ownedListener;
    private readonly ILogger _logger;

    internal EventCallbackListener(
        IObservable<HttpRequestResponse> requests,
        ResponseSender respond,
        ILogger logger,
        TcpListener? ownedListener = null)
    {
        _logger = logger;
        _ownedListener = ownedListener;

        _subscription = requests
            .Where(r => r.MessageType == MessageType.Request
                && string.Equals(r.Method, "NOTIFY", StringComparison.OrdinalIgnoreCase))
            .SelectMany(request => Observable
                .FromAsync(ct => HandleAsync(request, respond, ct))
                .Catch((Exception e) =>
                {
                    // One bad NOTIFY must not kill the callback stream.
                    _logger.LogDebug(e, "Handling a NOTIFY failed.");
                    return Observable.Empty<Unit>();
                }))
            .Subscribe(
                _ => { },
                e => _logger.LogError(e, "The event callback stream terminated."));
    }

    /// <summary>The bound port - resolved even when created with port 0 (ephemeral).</summary>
    internal int Port { get; private init; }

    /// <summary>
    /// Creates the real listener: binds immediately (so the ephemeral port is
    /// known for CALLBACK headers) and serves via SimpleHttpListener.Rx.
    /// </summary>
    internal static EventCallbackListener Create(int port, ILogger logger, CancellationToken lifetime)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        var requests = listener.ToHttpListenerObservable(lifetime, ErrorCorrection.HeaderCompletionError);

        return new EventCallbackListener(
            requests,
            (request, response, ct) => HttpSender.SendResponseAsync(request, response, null, ct),
            logger,
            listener)
        {
            Port = ((IPEndPoint)listener.LocalEndpoint).Port
        };
    }

    /// <summary>Routes NOTIFYs for one callback-path token; dispose to unregister.</summary>
    internal IDisposable Register(string token, Func<NotifyRequest, CancellationToken, Task> handler)
    {
        _routes[token] = handler;
        return Disposable.Create(() => _routes.TryRemove(token, out _));
    }

    private async Task HandleAsync(HttpRequestResponse request, ResponseSender respond, CancellationToken ct)
    {
        // UDA 2.0 4.3.3 error table: missing NT/NTS -> 400; wrong NT/NTS value
        // or missing/unknown SID -> 412.
        var nt = request.Headers.TryGetValue("NT", out var rawNt) ? rawNt.Trim() : null;
        var nts = request.Headers.TryGetValue("NTS", out var rawNts) ? rawNts.Trim() : null;

        if (nt is null || nts is null)
        {
            await respond(request, new HttpResponse { StatusCode = 400, ReasonPhrase = "Bad Request" }, ct)
                .ConfigureAwait(false);
            return;
        }

        var sid = request.Headers.TryGetValue("SID", out var rawSid) ? rawSid.Trim() : null;
        var token = request.Path?.TrimEnd('/').Split('/')[^1] ?? string.Empty;

        if (!string.Equals(nt, "upnp:event", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(nts, "upnp:propchange", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(sid)
            || !_routes.TryGetValue(token, out var handler))
        {
            await respond(request, new HttpResponse { StatusCode = 412, ReasonPhrase = "Precondition Failed" }, ct)
                .ConfigureAwait(false);
            return;
        }

        var seq = GenaHeaders.ParseSeq(request.Headers.TryGetValue("SEQ", out var rawSeq) ? rawSeq : null);
        var body = Encoding.UTF8.GetString(request.Body.Span);

        await handler(new NotifyRequest(sid, seq, body), ct).ConfigureAwait(false);
        await respond(request, new HttpResponse { StatusCode = 200 }, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _ownedListener?.Dispose();
    }
}
