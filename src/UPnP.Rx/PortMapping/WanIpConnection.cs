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
/// <para>
/// <b>There is no more source than this.</b> If you are looking for the method bodies, they
/// do not exist as a file you can edit - <c>Scpd/WANIPConnection1.scpd.xml</c> is the source
/// of truth and its header explains what editing it does. To read the emitted code, build and
/// look under <c>obj/&lt;config&gt;/net10.0/generated/</c>, or open the checked-in snapshot at
/// <c>tests/UPnP.Rx.Analyzers.Tests/Snapshots/WanIpConnection.verified.cs</c>, which a test
/// holds byte-identical to what the compiler actually sees.
/// </para>
/// </remarks>
[ScpdService("WANIPConnection1.scpd.xml")]
public sealed partial class WanIpConnection;
