using SSDP.UPnP.PCL.Model;

namespace UPnP.Rx;

/// <summary>
/// A device announced on the network: the SSDP discovery envelope plus lazy
/// access to its parsed description. Emitted by
/// <see cref="UpnpClient.DiscoverDevices"/> and <see cref="UpnpClient.DeviceLost"/>.
/// </summary>
public sealed class DiscoveredDevice
{
    private readonly Func<CancellationToken, Task<DescribedDevice>> _describe;

    internal DiscoveredDevice(
        USN? usn,
        Uri? location,
        Server? server,
        uint bootId,
        int? configId,
        bool hasParsingError,
        Func<CancellationToken, Task<DescribedDevice>> describe)
    {
        Usn = usn;
        Location = location;
        Server = server;
        BootId = bootId;
        ConfigId = configId;
        HasParsingError = hasParsingError;
        _describe = describe;
    }

    /// <summary>The announced unique service name (<c>USN</c> header), when parsable.</summary>
    public USN? Usn { get; }

    /// <summary>
    /// The device description URL (<c>LOCATION</c>). Always set for devices from
    /// <see cref="UpnpClient.DiscoverDevices"/>; may be <see langword="null"/> for
    /// <see cref="UpnpClient.DeviceLost"/> notices (byebye messages carry no location).
    /// </summary>
    public Uri? Location { get; }

    /// <summary>The device's identity from the <c>SERVER</c> header, when present.</summary>
    public Server? Server { get; }

    /// <summary>The device's boot instance (<c>BOOTID.UPNP.ORG</c>); changes when the device reboots.</summary>
    public uint BootId { get; }

    /// <summary>The device's configuration number (<c>CONFIGID.UPNP.ORG</c>); changes when its description changes.</summary>
    public int? ConfigId { get; }

    /// <summary>
    /// Whether the SSDP message carrying this announcement had parse defects
    /// (leniency: degraded messages are surfaced, not dropped).
    /// </summary>
    public bool HasParsingError { get; }

    /// <summary>
    /// Fetches and parses the device's description document. Cached by
    /// <see cref="Location"/> + <see cref="ConfigId"/> across all discoveries from
    /// the same <see cref="UpnpClient"/>, so repeated announcements cost one fetch.
    /// </summary>
    /// <exception cref="UpnpException">No location was announced, the fetch fails, or the document identifies no device.</exception>
    public Task<DescribedDevice> GetDescriptionAsync(CancellationToken ct = default) => _describe(ct);
}
