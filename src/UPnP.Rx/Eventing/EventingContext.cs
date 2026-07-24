using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace UPnP.Rx.Eventing;

/// <summary>
/// The client-wide eventing infrastructure: one lazily-started callback
/// listener, one transport, and one shared <see cref="GenaSubscriptionSource"/>
/// per event endpoint. Owned and disposed by <see cref="UpnpClient"/>.
/// </summary>
internal sealed class EventingContext(
    HttpClient httpClient,
    UpnpClientOptions options,
    CancellationToken clientLifetime) : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Uri, GenaSubscriptionSource> _sources = new();
    private readonly Lock _listenerLock = new();
    private HttpGenaTransport? _transport;
    private EventCallbackListener? _listener;
    private bool _disposed;

    /// <summary>
    /// The shared event stream for one service endpoint; created on first use.
    /// </summary>
    /// <param name="eventSubUrl">The service's absolute event subscription URL.</param>
    /// <param name="localAddress">
    /// Our address on the network shared with the device (from the discovery
    /// envelope); resolved via a route lookup when unknown.
    /// </param>
    internal IObservable<UpnpEvent> GetOrCreateSource(Uri eventSubUrl, IPAddress? localAddress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _sources.GetOrAdd(eventSubUrl, url =>
        {
            var listener = EnsureListener();
            var callbackHost = (LocalRoute.IsUsable(localAddress) ? localAddress : LocalRoute.Resolve(url))
                ?? throw new UpnpException(
                    $"No local address faces {url.Host} - cannot offer the device an event callback URL.");

            return new GenaSubscriptionSource(
                url,
                token => new Uri($"http://{callbackHost}:{listener.Port}/upnp/events/{token}"),
                _transport!,
                listener.Register,
                options,
                options.Logger,
                clientLifetime);
        });
    }

    private EventCallbackListener EnsureListener()
    {
        lock (_listenerLock)
        {
            _transport ??= new HttpGenaTransport(httpClient, options);
            return _listener ??= EventCallbackListener.Create(
                options.EventCallbackPort, options.Logger, clientLifetime);
        }
    }

    /// <summary>Abrupt: releases the listener; engines die via the client lifetime token.</summary>
    public void Dispose()
    {
        _disposed = true;
        _listener?.Dispose();
    }

    /// <summary>
    /// Graceful: stops every subscription engine and awaits its in-task goodbye
    /// (the UNSUBSCRIBE, bounded by <see cref="UpnpClientOptions.ActionTimeout"/>)
    /// while the HttpClient is still usable, then releases the listener.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        foreach (var source in _sources.Values)
        {
            try
            {
                await source.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                options.Logger.LogDebug(e, "An event subscription's goodbye failed during disposal.");
            }
        }

        _listener?.Dispose();
    }
}
