using UPnP.Rx.Model;

namespace UPnP.Rx.Eventing;

/// <summary>
/// The outbound half of GENA (SUBSCRIBE / renewal / UNSUBSCRIBE), abstracted so
/// the subscription engine tests against a scripted fake - the same seam
/// discipline as <c>IControlPoint</c> and the callback listener.
/// </summary>
internal interface IGenaTransport
{
    /// <summary>Initial SUBSCRIBE; returns the granted SID and timeout (null timeout = device said infinite/unparsable).</summary>
    Task<(string Sid, TimeSpan? Timeout)> SubscribeAsync(
        Uri eventSubUrl, Uri callback, TimeSpan requestedTimeout, CancellationToken ct);

    /// <summary>Renewal SUBSCRIBE for an existing SID.</summary>
    Task RenewAsync(Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct);

    /// <summary>UNSUBSCRIBE; best-effort goodbye.</summary>
    Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct);
}

/// <summary>Production transport: GENA verbs over the client's HttpClient.</summary>
internal sealed class HttpGenaTransport(HttpClient httpClient, UpnpClientOptions options) : IGenaTransport
{
    private static readonly HttpMethod _subscribe = new("SUBSCRIBE");
    private static readonly HttpMethod _unsubscribe = new("UNSUBSCRIBE");

    public async Task<(string Sid, TimeSpan? Timeout)> SubscribeAsync(
        Uri eventSubUrl, Uri callback, TimeSpan requestedTimeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(_subscribe, eventSubUrl);
        request.Headers.TryAddWithoutValidation("CALLBACK", GenaHeaders.ComposeCallback(callback));
        request.Headers.TryAddWithoutValidation("NT", "upnp:event");
        request.Headers.TryAddWithoutValidation("TIMEOUT", GenaHeaders.ComposeTimeout(requestedTimeout));

        using var response = await SendAsync(request, "SUBSCRIBE", ct).ConfigureAwait(false);

        var sid = response.Headers.TryGetValues("SID", out var sids) ? sids.FirstOrDefault()?.Trim() : null;

        if (string.IsNullOrEmpty(sid))
        {
            throw new UpnpException("The device accepted the SUBSCRIBE but returned no SID.");
        }

        var timeout = response.Headers.TryGetValues("TIMEOUT", out var timeouts)
            ? GenaHeaders.ParseTimeout(timeouts.FirstOrDefault())
            : null;

        return (sid, timeout);
    }

    public async Task RenewAsync(Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(_subscribe, eventSubUrl);
        request.Headers.TryAddWithoutValidation("SID", sid);
        request.Headers.TryAddWithoutValidation("TIMEOUT", GenaHeaders.ComposeTimeout(requestedTimeout));

        using var response = await SendAsync(request, "renewal", ct).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(_unsubscribe, eventSubUrl);
        request.Headers.TryAddWithoutValidation("SID", sid);

        using var response = await SendAsync(request, "UNSUBSCRIBE", ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(options.ActionTimeout, options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new UpnpException($"The {operation} request timed out after {options.ActionTimeout}.");
        }
        catch (HttpRequestException e)
        {
            throw new UpnpException($"The {operation} request failed: {e.Message}", e);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new UpnpException($"The {operation} request was refused with HTTP {status}.");
        }

        return response;
    }
}
