using System.Collections.Concurrent;
using System.Net;

namespace UPnP.Rx.Eventing;

/// <summary>
/// The client-wide eventing infrastructure: one lazily-started callback
/// listener, one transport, and one shared <see cref="GenaSubscriptionSource"/> per
/// event endpoint <i>and</i> device configuration - a device re-described under a new
/// CONFIGID gets its own, because the old one's cancellation logic reasons about the
/// CONFIGID it was built with. Bounded by endpoints times configurations observed,
/// which in practice is one each. Owned and disposed by <see cref="UpnpClient"/>.
/// </summary>
internal sealed class EventingContext(
    HttpClient httpClient,
    UpnpClientOptions options,
    Func<IObservable<UPnP.Rx.Presence.RosterChange>> presence,
    CancellationToken clientLifetime) : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(Uri EventSubUrl, DeviceIdentity Identity), GenaSubscriptionSource> _sources = new();
    private readonly Lock _listenerLock = new();
    private HttpGenaTransport? _transport;
    private EventCallbackListener? _listener;
    private bool _disposed;

    /// <summary>
    /// The shared event stream for one service endpoint at one device configuration;
    /// created on first use.
    /// </summary>
    /// <param name="eventSubUrl">The service's absolute event subscription URL.</param>
    /// <param name="localAddress">
    /// Our address on the network shared with the device (from the discovery
    /// envelope); resolved via a route lookup when unknown.
    /// </param>
    /// <param name="identity">
    /// Which device is behind the endpoint, so the subscription can notice the
    /// presence changes UDA 2.0 clause 4.1.1 says cancel it.
    /// </param>
    internal IObservable<UpnpEvent> GetOrCreateSource(
        Uri eventSubUrl, IPAddress? localAddress, DeviceIdentity identity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Keyed by endpoint AND identity: the same eventSubURL can be re-described
        // under a new CONFIGID, and the cached source's MayResubscribe decision is
        // evaluated against the CONFIGID it was built with. A stale one would answer
        // the "has the description moved" question with the wrong evidence.
        return _sources.GetOrAdd((eventSubUrl, identity), key =>
        {
            var url = key.EventSubUrl;
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
                identity,
                presence,
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

        try
        {
            // Goodbyes in parallel (review RX-5): each is already bounded by
            // ActionTimeout, so the whole graceful path is too.
            await Task.WhenAll(_sources.Values.Select(source => source.ShutdownAsync()))
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            options.Logger.EventGoodbyeFailed(e);
        }

        _listener?.Dispose();
    }
}
