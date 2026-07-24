namespace UPnP.Rx.PortMapping;

/// <summary>
/// The WAN connection state reported by the gateway's <c>GetStatusInfo</c>
/// action. Immutable.
/// </summary>
public sealed record ConnectionStatusInfo
{
    /// <summary>
    /// The raw connection status (<c>NewConnectionStatus</c>): <c>Connected</c>,
    /// <c>Disconnected</c>, <c>Connecting</c>, <c>Unconfigured</c>, …
    /// </summary>
    public string? Status { get; init; }

    /// <summary>Whether the WAN connection is up (<see cref="Status"/> is <c>Connected</c>).</summary>
    public bool IsConnected =>
        string.Equals(Status, "Connected", StringComparison.OrdinalIgnoreCase);

    /// <summary>The last connection error (<c>NewLastConnectionError</c>); <c>ERROR_NONE</c> when healthy.</summary>
    public string? LastError { get; init; }

    /// <summary>How long the connection has been up (<c>NewUptime</c>).</summary>
    public TimeSpan Uptime { get; init; }
}
