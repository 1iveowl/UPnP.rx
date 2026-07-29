using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace UPnP.Rx.Analyzers;

/// <summary>
/// UPNPRX001 - an IGD port-mapping lease duration outside the range the standardized
/// service template allows.
/// </summary>
/// <remarks>
/// <para>
/// <c>WANIPConnection</c> declares <c>PortMappingLeaseDuration</c> as a <c>ui4</c> with
/// <c>allowedValueRange</c> 0-604800, and zero means "indefinite". Two things go wrong
/// either side of that range, and they are not equally loud:
/// </para>
/// <para>
/// Above it, the gateway refuses - noisy, and you find out. Below it is the reason this
/// rule exists: .NET saturates floating-point to integer conversions, so a negative
/// <see cref="TimeSpan"/> became <c>(uint)0</c> on the wire, and zero is IGD's encoding
/// for a mapping that never expires. Asking for a lease in the past asked for a permanent
/// hole in the firewall, silently. 6.0.0 P3 made that throw at run time; this reports it
/// at build time, for values source can see.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LeaseDurationAnalyzer : DiagnosticAnalyzer
{
    private const string _id = DiagnosticIds.LeaseDurationOutOfRange;

    /// <summary>The methods whose <c>lease</c> argument this rule reads.</summary>
    private static readonly HashSet<string> _leaseMethods =
        new(StringComparer.Ordinal) { "AddPortMappingAsync", "AddAnyPortMappingAsync" };

    // Hoisted: analyzers run on every keystroke, so a per-diagnostic allocation here is
    // an allocation per keystroke per call site.
    private static readonly ImmutableDictionary<string, string?> _below =
        ImmutableDictionary<string, string?>.Empty.Add(DiagnosticIds.Properties.Direction, "Below");

    private static readonly ImmutableDictionary<string, string?> _above =
        ImmutableDictionary<string, string?>.Empty.Add(DiagnosticIds.Properties.Direction, "Above");

    private static readonly DiagnosticDescriptor _rule = new(
        _id,
        title: "Port-mapping lease duration is outside the range IGD allows",
        messageFormat:
            "A lease of {0} is outside the 0-604800 seconds IGD allows{1}",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "IGD carries a port-mapping lease as a ui4 of seconds, ranged 0-604800 by the "
            + "standardized service template, where zero means indefinite. A negative lease is "
            + "the dangerous end: converting it for the wire saturates to zero, so the gateway "
            + "is asked for a permanent mapping instead of a short one. Use "
            + "LeaseDurations.Indefinite when that is what you meant.",
        helpLinkUri: DiagnosticIds.HelpLink(_id));

    /// <inheritdoc />
    /// <remarks>
    /// <c>ImmutableArray.Create</c> rather than a collection expression: netstandard2.0
    /// resolves an older <c>ImmutableArray&lt;T&gt;</c> that the compiler refuses to build
    /// one from (CS9210).
    /// </remarks>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(_rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
    }

    /// <summary>The <c>lease</c> argument of an <c>AddPortMapping</c>-shaped call.</summary>
    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (!_leaseMethods.Contains(invocation.TargetMethod.Name)
            || !IsPortMappingType(invocation.TargetMethod.ContainingType))
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            // By parameter name, not position: the overloads differ, and IInternetGateway,
            // InternetGateway and PortMapper all spell it "lease".
            if (argument.Parameter?.Name == "lease")
            {
                Report(context, argument.Value);
                return;
            }
        }
    }

    /// <summary><c>LeaseDuration = …</c> in an object initializer or a <c>with</c> expression.</summary>
    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        if (assignment.Target is IPropertyReferenceOperation { Property: { Name: "LeaseDuration" } property }
            && IsPortMappingType(property.ContainingType))
        {
            Report(context, assignment.Value);
        }
    }

    private static void Report(OperationAnalysisContext context, IOperation value)
    {
        if (TimeSpanValue.TryRead(value) is not { } lease || IsInRange(lease))
        {
            // Not readable from source, or fine. Both are silence: the budget is zero
            // false positives, and a high false-negative rate is the price.
            return;
        }

        var below = lease < TimeSpan.Zero;

        context.ReportDiagnostic(Diagnostic.Create(
            _rule,
            value.Syntax.GetLocation(),
            below ? _below : _above,
            Describe(lease),
            below
                ? " - a negative lease is sent as zero, which asks the gateway for a permanent mapping"
                : string.Empty));
    }

    private static bool IsInRange(TimeSpan lease) =>
        lease >= TimeSpan.Zero && lease.TotalSeconds <= 604_800;

    private static string Describe(TimeSpan lease) =>
        lease.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds";

    /// <summary>Whether the symbol belongs to this library's port-mapping surface.</summary>
    private static bool IsPortMappingType(INamedTypeSymbol? type) =>
        type?.ContainingNamespace?.ToDisplayString() == "UPnP.Rx.PortMapping";
}
