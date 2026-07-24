using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SSDP.UPnP.PCL.Model;

namespace UPnP.Rx;

/// <summary>
/// Configuration for <see cref="UpnpClient"/> (decision 6). Immutable; create
/// with an object initializer and derive variants with <c>with</c>.
/// </summary>
public sealed record UpnpClientOptions
{
    /// <summary>
    /// The search target used by <see cref="UpnpClient.DiscoverDevices"/> when no
    /// per-call target is given. Defaults to <see cref="SearchTargets.RootDevice"/>.
    /// </summary>
    public ST DefaultSearchTarget { get; init; } = SearchTargets.RootDevice;

    /// <summary>
    /// The default maximum response delay (M-SEARCH <c>MX</c>); UDA 2.0 limits it
    /// to 1–5 seconds. Defaults to 3 seconds.
    /// </summary>
    public TimeSpan DefaultMx { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The control point friendly name sent as <c>CPFN.UPNP.ORG</c> — required by
    /// UDA 2.0 on multicast searches.
    /// </summary>
    public string ControlPointFriendlyName { get; init; } = "UPnP.Rx";

    /// <summary>Timeout for fetching and parsing description documents (DDD and SCPD).</summary>
    public TimeSpan DescriptionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for SOAP action calls.</summary>
    public TimeSpan ActionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The clock all timeouts run on; inject a fake in tests. Init-only by design
    /// (a settable clock is mutable ambient state — see plan §9).
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Receives dropped-message and degraded-input notes; defaults to no logging.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;
}
