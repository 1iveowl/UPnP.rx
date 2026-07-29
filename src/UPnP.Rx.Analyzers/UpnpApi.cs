using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace UPnP.Rx.Analyzers;

/// <summary>
/// What the rules know about the library they guard: the names they bind on, and how they
/// spell a duration back to the reader.
/// </summary>
/// <remarks>
/// <para>
/// Extracted in the 6.0.0 review pass. Two rules were carrying identical copies of the
/// port-mapping namespace, the set of lease-taking method names, and the seconds formatter -
/// which is exactly the "structural twins" this repo consolidates, as opposed to the
/// per-rule pipeline shape, which is idiom and stays duplicated
/// (<see href="https://github.com/1iveowl/UPnP.rx/blob/main/plan/DECISIONS.md">DECISIONS.md</see>,
/// 2026-07-26).
/// </para>
/// <para>
/// These names are a contract with the library that the compiler cannot check, because a
/// netstandard2.0 analyzer cannot reference a net10.0 assembly. <c>StubGuardTests</c> asserts
/// every one of them against the real types by reflection, so a rename is a red build rather
/// than a rule that quietly stops firing.
/// </para>
/// </remarks>
internal static class UpnpApi
{
    /// <summary>The namespace the port-mapping surface lives in.</summary>
    public const string PortMappingNamespace = "UPnP.Rx.PortMapping";

    /// <summary>The options record UPNPRX002 reads.</summary>
    public const string OptionsType = "UPnP.Rx.UpnpClientOptions";

    /// <summary>The parameter that carries a lease; matched by name, not position.</summary>
    public const string LeaseParameter = "lease";

    /// <summary>The methods that create a port mapping and hand back a lease.</summary>
    public static readonly HashSet<string> LeaseMethods =
        new(StringComparer.Ordinal) { "AddPortMappingAsync", "AddAnyPortMappingAsync" };

    /// <summary>Whether the symbol belongs to this library's port-mapping surface.</summary>
    /// <remarks>
    /// By containing namespace rather than by name: a consumer is entitled to their own
    /// <c>IInternetGateway</c> with an <c>AddPortMappingAsync(… TimeSpan lease …)</c>, and
    /// reporting on it would be a false positive of exactly the kind the budget forbids.
    /// </remarks>
    public static bool IsPortMappingType(INamedTypeSymbol? type) =>
        type?.ContainingNamespace?.ToDisplayString() == PortMappingNamespace;

    /// <summary>Whether the symbol is <see cref="OptionsType"/> itself.</summary>
    public static bool IsOptionsType(INamedTypeSymbol? type) =>
        type?.ToDisplayString() == OptionsType;

    /// <summary>
    /// A duration as the diagnostic message spells it. Seconds throughout, because that is
    /// the unit every range in this domain is stated in - the reader should not have to
    /// convert the message back to compare it with the limit beside it.
    /// </summary>
    public static string DescribeSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds";
}
