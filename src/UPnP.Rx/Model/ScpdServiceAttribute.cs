namespace UPnP.Rx.Model;

/// <summary>
/// Marks a <see langword="partial"/> class as a typed wrapper generated from a checked-in
/// SCPD document: one method per action, with the argument names and types the document
/// declares, over the same <see cref="IUpnpService.InvokeAsync"/> everything else uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>The generated wrapper describes the document, not the device.</b> The SCPD you check
/// in is the standardized service template, and real devices deviate from it - that is the
/// entire reason this library's parsers are lenient. A generated method compiling is
/// evidence that <em>the template</em> declares that action with those arguments, and no
/// evidence at all that the device in front of you implements it. It can still answer with
/// a SOAP fault, and <see cref="UpnpActionException"/> is still the way you find out.
/// </para>
/// <para>
/// What it does buy: a typo in an action or argument name becomes a compile error instead
/// of a fault from a router, and the ranges the document declares are checked before
/// anything reaches the network.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [ScpdService("WANIPConnection1.scpd.xml")]
/// public sealed partial class WanIpConnection;
/// </code>
/// with the document supplied to the compiler as an <c>AdditionalFiles</c> item:
/// <code>
/// &lt;AdditionalFiles Include="Scpd/WANIPConnection1.scpd.xml" /&gt;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ScpdServiceAttribute : Attribute
{
    /// <summary>Marks a partial class for generation from the named SCPD document.</summary>
    /// <param name="scpdFileName">
    /// The file name of an <c>AdditionalFiles</c> SCPD document, matched on the file name
    /// alone so the path in the project file can move without breaking the reference.
    /// </param>
    public ScpdServiceAttribute(string scpdFileName) => ScpdFileName = scpdFileName;

    /// <summary>The SCPD document this wrapper is generated from.</summary>
    public string ScpdFileName { get; }
}
