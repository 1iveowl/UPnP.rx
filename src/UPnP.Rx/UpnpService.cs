using System.Text;
using UPnP.Rx.Model;
using UPnP.Rx.Parsing;

namespace UPnP.Rx;

/// <summary>
/// A controllable service on a described device: fetches its SCPD lazily and
/// invokes its SOAP actions (UDA 2.0 clause 3). Instances are created by
/// <see cref="DescribedDevice"/> and are stable per service, so the SCPD is
/// fetched at most once.
/// </summary>
public sealed class UpnpService
{
    /// <summary>UDA 2.0 §3.2.1: the charset parameter is quoted; some devices 415 on the unquoted form.</summary>
    private const string _soapContentType = "text/xml; charset=\"utf-8\"";

    /// <summary>UDA 2.0 §3.2.1 product-token form: OS/version UPnP/2.0 product/version.</summary>
    private static readonly string _userAgent =
        $"{Environment.OSVersion.Platform}/{Environment.OSVersion.Version} UPnP/2.0 " +
        $"UPnP.Rx/{typeof(UpnpService).Assembly.GetName().Version?.ToString(2) ?? "1.0"}";

    private readonly HttpClient _httpClient;
    private readonly UpnpClientOptions _options;
    private readonly CancellationToken _lifetime;
    private readonly Lock _scpdLock = new();
    private Task<Scpd>? _scpdTask;

    internal UpnpService(
        ServiceDescription description,
        HttpClient httpClient,
        UpnpClientOptions options,
        CancellationToken lifetime)
    {
        Description = description;
        _httpClient = httpClient;
        _options = options;
        _lifetime = lifetime;
    }

    /// <summary>The service entry from the device description document.</summary>
    public ServiceDescription Description { get; }

    /// <summary>
    /// The service's SCPD, fetched from <see cref="ServiceDescription.ScpdUrl"/>
    /// and cached for the lifetime of this instance. Only a successful fetch is
    /// cached — a transient failure is retried on the next call.
    /// </summary>
    /// <exception cref="UpnpException">The service declares no SCPD URL, the fetch fails, or the document is not parsable.</exception>
    public async Task<Scpd> GetScpdAsync(CancellationToken ct = default)
    {
        Task<Scpd> task;

        lock (_scpdLock)
        {
            if (_scpdTask is null || _scpdTask.IsFaulted || _scpdTask.IsCanceled)
            {
                _scpdTask = FetchScpdAsync();
            }

            task = _scpdTask;
        }

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Invokes a SOAP action on the service (<c>POST</c> to
    /// <see cref="ServiceDescription.ControlUrl"/> with the <c>SOAPACTION</c>
    /// header), returning its out-arguments.
    /// </summary>
    /// <param name="action">The action name, as declared in the SCPD.</param>
    /// <param name="arguments">
    /// The in-arguments by name. UDA 2.0 §3.2.1 requires <em>every</em> in-argument
    /// of the action to be present, in SCPD declaration order — pass an
    /// insertion-ordered dictionary containing all of them (empty strings for
    /// wildcards).
    /// </param>
    /// <param name="ct">Cancels the call; the options' <see cref="UpnpClientOptions.ActionTimeout"/> also applies.</param>
    /// <exception cref="UpnpActionException">The device answered with a SOAP fault; carries the <see cref="UpnpError"/>.</exception>
    /// <exception cref="UpnpException">The service declares no control URL or service type, the HTTP exchange fails, or the response is unparsable.</exception>
    public async Task<ActionResult> InvokeAsync(
        string action,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (Description.ControlUrl is null)
        {
            throw new UpnpException($"The service {Description.ServiceType} declares no controlURL.");
        }

        if (Description.ServiceType is null)
        {
            throw new UpnpException("The service declares no serviceType, required for the SOAPACTION header.");
        }

        var envelope = SoapComposer.ComposeActionRequest(Description.ServiceType, action, arguments);

        using var timeout = new CancellationTokenSource(_options.ActionTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _lifetime);

        string body;
        System.Net.HttpStatusCode status;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Description.ControlUrl);
            var content = new StringContent(envelope, Encoding.UTF8);
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", _soapContentType);
            request.Content = content;
            request.Headers.TryAddWithoutValidation(
                "SOAPACTION", SoapComposer.ComposeSoapActionHeader(Description.ServiceType, action));
            request.Headers.TryAddWithoutValidation("USER-AGENT", _userAgent);

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);

            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new UpnpException($"The action {action} timed out after {_options.ActionTimeout}.");
        }
        catch (HttpRequestException e)
        {
            throw new UpnpException($"The action {action} failed: {e.Message}", e);
        }

        // Faults are served with status 500 per UDA 2.0, but be lenient and
        // recognize a fault body regardless of status.
        var fault = SoapParser.ParseFault(body);

        if (fault.IsSuccess)
        {
            throw new UpnpActionException(
                $"The action {action} failed with UPnP error {fault.Value.Code} ({fault.Value.Description}).",
                fault.Value);
        }

        var result = SoapParser.ParseActionResponse(body, action);

        if (result.IsSuccess)
        {
            return result.Value;
        }

        throw new UpnpException(
            $"The action {action} returned HTTP {(int)status} with an unparsable body: {result.Error}");
    }

    private async Task<Scpd> FetchScpdAsync()
    {
        if (Description.ScpdUrl is null)
        {
            throw new UpnpException($"The service {Description.ServiceType} declares no SCPDURL.");
        }

        using var timeout = new CancellationTokenSource(_options.DescriptionTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime);

        string xml;

        try
        {
            xml = await _httpClient.GetStringAsync(Description.ScpdUrl, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new UpnpException($"Fetching the SCPD from {Description.ScpdUrl} timed out.");
        }
        catch (HttpRequestException e)
        {
            throw new UpnpException($"Fetching the SCPD from {Description.ScpdUrl} failed: {e.Message}", e);
        }

        return ScpdParser.ParseScpd(xml).Match(
            scpd => scpd,
            error => throw new UpnpException($"The SCPD at {Description.ScpdUrl} is unparsable: {error}"));
    }
}
