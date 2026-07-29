using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace UPnP.Rx.Analyzers;

/// <summary>
/// UPNPRX002 - a <c>UpnpClientOptions</c> duration outside the range it documents.
/// </summary>
/// <remarks>
/// <para>
/// Only values that cannot be right anywhere. "Unusually short but positive" is
/// deliberately not reported: an <c>ActionTimeout</c> of 100 ms may be exactly right on a
/// fast LAN, and a rule that second-guesses that would be the kind people learn to
/// suppress - which would poison the quiet rules shipping beside it.
/// </para>
/// <para>
/// The direct analogue of the inherited <c>SSDP005</c> ("a device configuration value
/// outside its UDA 2.0 range"), one library down.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OptionRangeAnalyzer : DiagnosticAnalyzer
{
    private const string _id = DiagnosticIds.OptionOutOfRange;
    private const string _optionsType = "UPnP.Rx.UpnpClientOptions";

    /// <summary>
    /// The options this rule reads, and why each has a floor. Anything not listed is not
    /// this rule's business - <c>EventCallbackPort</c> in particular has no entry because
    /// <see langword="ushort"/> already makes its range unrepresentable.
    /// </summary>
    private static readonly Dictionary<string, Bound> _bounds = new(StringComparer.Ordinal)
    {
        ["DescriptionTimeout"] = new(
            TimeSpan.Zero,
            Inclusive: false,
            "a non-positive timeout cancels immediately, so every description fetch fails before it starts"),
        ["ActionTimeout"] = new(
            TimeSpan.Zero,
            Inclusive: false,
            "a non-positive timeout cancels immediately, so every SOAP call fails before it starts"),
        ["RosterExpiryFallback"] = new(
            TimeSpan.Zero,
            Inclusive: false,
            "a non-positive fallback expires every device that announces no usable max-age the moment it arrives"),
        ["EventSubscriptionTimeout"] = new(
            TimeSpan.FromSeconds(1),
            Inclusive: true,
            "GENA carries the timeout as whole seconds, so anything under one second composes 'TIMEOUT: Second-0'")
    };

    private static readonly DiagnosticDescriptor _rule = new(
        _id,
        title: "UpnpClientOptions value is outside its documented range",
        messageFormat: "{0} of {1} is outside its documented range - {2}",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "UpnpClientOptions documents a range for each of its durations, and the initializer "
            + "throws for values outside it. This reports the ones source can see, at build time "
            + "rather than at first construction. Only provably wrong values are reported; a short "
            + "but positive timeout is a legitimate choice and is left alone.",
        helpLinkUri: DiagnosticIds.HelpLink(_id));

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(_rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Covers object initializers and `with` expressions alike: both compile to a
        // simple assignment onto the property.
        context.RegisterOperationAction(Analyze, OperationKind.SimpleAssignment);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        if (assignment.Target is not IPropertyReferenceOperation property
            || property.Property.ContainingType?.ToDisplayString() != _optionsType
            || !_bounds.TryGetValue(property.Property.Name, out var bound))
        {
            return;
        }

        if (TimeSpanValue.TryRead(assignment.Value) is not { } value || bound.Allows(value))
        {
            // Unreadable from source, or fine. Both are silence.
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            _rule,
            assignment.Value.Syntax.GetLocation(),
            ImmutableDictionary<string, string?>.Empty
                .Add(DiagnosticIds.Properties.OptionName, property.Property.Name),
            property.Property.Name,
            Describe(value),
            bound.Reason));
    }

    private static string Describe(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds";

    /// <summary>A floor, and the consequence of going under it.</summary>
    /// <param name="Minimum">The smallest value that could ever be right.</param>
    /// <param name="Inclusive">Whether <paramref name="Minimum"/> itself is allowed.</param>
    /// <param name="Reason">What goes wrong below it, phrased for the diagnostic message.</param>
    private readonly record struct Bound(TimeSpan Minimum, bool Inclusive, string Reason)
    {
        /// <summary>Whether <paramref name="value"/> clears this floor.</summary>
        public bool Allows(TimeSpan value) => Inclusive ? value >= Minimum : value > Minimum;
    }
}
