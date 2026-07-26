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

/// <summary>
/// A GENA request the device answered with a non-success HTTP status - carries
/// the code so the engine can tell permanent refusals (405/501) from transient
/// failures.
/// </summary>
internal sealed class GenaHttpException(string message, int statusCode) : UpnpException(message)
{
    /// <summary>The HTTP status the device answered with.</summary>
    internal int StatusCode { get; } = statusCode;
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
        var response = await TimedExchange.RunAsync(
            token => httpClient.SendAsync(request, token),
            options.ActionTimeout, options.TimeProvider, lifetime: CancellationToken.None, ct,
            timeoutMessage: $"The {operation} request timed out after {options.ActionTimeout}.",
            failurePrefix: $"The {operation} request failed")
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new GenaHttpException($"The {operation} request was refused with HTTP {status}.", status);
        }

        return response;
    }
}
