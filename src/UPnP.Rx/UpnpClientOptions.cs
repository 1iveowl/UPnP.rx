using System.Runtime.CompilerServices;
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
    /// The default maximum response delay (M-SEARCH <c>MX</c>) used by
    /// <see cref="UpnpClient.DiscoverDevices"/> when no per-call value is given.
    /// Defaults to 3 seconds.
    /// </summary>
    /// <remarks>
    /// UDA 2.0 clause 1.3.2 states the floor and the ceiling differently, and so does
    /// the type: <see cref="MxSeconds"/> <em>enforces</em> the "shall be at least 1"
    /// and deliberately does not enforce the "should be at most 5", because the same
    /// clause permits raising it "if a large number of devices are expected to
    /// respond". The ceiling is reported by the <c>SSDP001</c> analyzer instead, which
    /// ships with SSDP.UPnP.PCL and sees your literal at your call site.
    /// </remarks>
    public MxSeconds DefaultMx { get; init; } = new(3);

    /// <summary>
    /// The control point friendly name sent as <c>CPFN.UPNP.ORG</c> — required by
    /// UDA 2.0 on multicast searches.
    /// </summary>
    public string ControlPointFriendlyName { get; init; } = "UPnP.Rx";

    /// <summary>
    /// Timeout for fetching and parsing description documents (DDD and SCPD).
    /// Must be positive: a non-positive timeout cancels immediately, so every
    /// description fetch would fail before it started.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan DescriptionTimeout
    {
        get;
        init => field = Positive(value);
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for SOAP action calls. Must be positive, for the same reason as
    /// <see cref="DescriptionTimeout"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan ActionTimeout
    {
        get;
        init => field = Positive(value);
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The local TCP port for the GENA event callback listener; 0 (the default)
    /// binds an ephemeral port. Set a fixed port for firewall rules.
    /// </summary>
    /// <remarks>
    /// A <see cref="ushort"/> because that is the range a TCP port has - the type
    /// is the whole validation. Ports below 1024 need privileges on most systems
    /// and will fail to bind, which is a run-time fact about the host rather than
    /// something a range can decide.
    /// </remarks>
    public ushort EventCallbackPort { get; init; }

    /// <summary>
    /// The subscription duration requested from devices (<c>TIMEOUT: Second-n</c>);
    /// the granted value is renewed automatically at half-life.
    /// </summary>
    /// <remarks>
    /// At least one second, because GENA carries it as whole seconds
    /// (UDA 2.0 clause 4.1.2): a sub-second value composes <c>TIMEOUT: Second-0</c>
    /// and a negative one composes <c>Second--5</c>. UDA 2.0 additionally recommends
    /// 1800 seconds or more, which is a recommendation and not enforced here.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is under one second.</exception>
    public TimeSpan EventSubscriptionTimeout
    {
        get;
        init => field = AtLeastOneSecond(value);
    } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether event subscriptions recover automatically from renewal failures
    /// and SEQ gaps (surfaced as <c>RenewalFailed</c>/<c>Resubscribed</c>/<c>GapDetected</c>
    /// events). When off, such failures terminate the stream with <c>OnError</c>.
    /// </summary>
    public bool AutoResubscribe { get; init; } = true;

    /// <summary>
    /// The advertisement lifetime <see cref="UpnpClient.Roster"/> assumes when a
    /// device announces no usable <c>CACHE-CONTROL: max-age</c> - either none at all,
    /// or a non-positive one, which a device that keeps announcing cannot have meant.
    /// Defaults to 30 minutes (the UDA 2.0 recommended advertisement duration).
    /// Must be positive: a non-positive fallback would expire every device that
    /// announces no usable lifetime the moment it arrives.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan RosterExpiryFallback
    {
        get;
        init => field = Positive(value);
    } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The clock all timeouts run on; inject a fake in tests. Init-only by design
    /// (a settable clock is mutable ambient state — see plan §9).
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Receives dropped-message and degraded-input notes; defaults to no logging.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>
    /// Throws at the initializer rather than letting a nonsense duration reach the
    /// wire or a <see cref="CancellationTokenSource"/>. The <c>UPNPRX002</c> analyzer
    /// reports the same thing at build time for literals; this catches the rest.
    /// </summary>
    private static TimeSpan Positive(TimeSpan value, [CallerMemberName] string? name = null) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive.");

    /// <inheritdoc cref="Positive"/>
    private static TimeSpan AtLeastOneSecond(TimeSpan value, [CallerMemberName] string? name = null) =>
        value >= TimeSpan.FromSeconds(1)
            ? value
            : throw new ArgumentOutOfRangeException(
                name, value, $"{name} must be at least one second; GENA carries it as whole seconds.");
}
