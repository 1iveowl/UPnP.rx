using UPnP.Rx.Model;

namespace UPnP.Rx.PortMapping;

/// <summary>
/// A typed wrapper over <c>WANIPConnection:1</c>, generated from the standardized service
/// description checked in at <c>Scpd/WANIPConnection1.scpd.xml</c>.
/// </summary>
/// <remarks>
/// Every member is generated - see <see cref="ScpdServiceAttribute"/> for what that does and
/// does not promise. In short: it describes the template, not the device in front of you, so
/// a call can still come back as a <see cref="UpnpActionException"/>.
/// <para>
/// <see cref="InternetGateway"/> remains the front door for port mapping; this is the
/// generated surface underneath it, and the byte-identity tests hold the two to the same
/// wire output.
/// </para>
/// </remarks>
[ScpdService("WANIPConnection1.scpd.xml")]
public sealed partial class WanIpConnection;
