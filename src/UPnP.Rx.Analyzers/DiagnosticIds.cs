namespace UPnP.Rx.Analyzers;

/// <summary>
/// The diagnostic IDs, and the code-fix property keys that travel with them.
/// </summary>
/// <remarks>
/// Linked into the code-fix project rather than referenced: an analyzer assembly may not
/// reference the Workspaces layer (RS1038), and a reverse reference cycles with packing.
/// A linked source file is the supported way to share these.
/// </remarks>
internal static class DiagnosticIds
{
    // Each ID is declared in the phase that implements it, not reserved in advance:
    // InfrastructureTests asserts every declared ID is actually reported by an analyzer,
    // so a reserved-but-unimplemented one is a red build rather than a rule that quietly
    // never fires. Planned: UPNPRX003 (P7).

    /// <summary>An IGD port-mapping lease duration outside the 0-604800 seconds the standardized service template allows.</summary>
    public const string LeaseDurationOutOfRange = "UPNPRX001";

    /// <summary>A <c>UpnpClientOptions</c> value outside its documented range.</summary>
    public const string OptionOutOfRange = "UPNPRX002";

    /// <summary>Where each rule is documented; the anchor matches the ID, lower-cased.</summary>
    public static string HelpLink(string id) =>
        "https://github.com/1iveowl/UPnP.rx#" + id.ToLowerInvariant();

    /// <summary>Diagnostic-property keys shared between an analyzer and its code fix.</summary>
    internal static class Properties
    {
        /// <summary>The offending value, in seconds, as written in the source.</summary>
        public const string Seconds = "Seconds";

        /// <summary>Which side of the range was crossed: <c>Below</c> or <c>Above</c>.</summary>
        public const string Direction = "Direction";

        /// <summary>The option property the diagnostic is about (UPNPRX002).</summary>
        public const string OptionName = "OptionName";
    }
}
