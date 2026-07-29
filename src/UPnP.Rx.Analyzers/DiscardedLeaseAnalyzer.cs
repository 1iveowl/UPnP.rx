using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace UPnP.Rx.Analyzers;

/// <summary>
/// UPNPRX003 - the lease returned by a port-mapping call is thrown away.
/// </summary>
/// <remarks>
/// <para>
/// The returned lease owns three things: the mapping on the router, a renewal loop running
/// for the life of the process, and - for the <c>PortMapper</c> one-liner - the whole
/// discovery chain including a <c>UpnpClient</c> and its sockets. Discarding it leaks all
/// three and leaves a hole in the firewall that nothing will close.
/// </para>
/// <para>
/// Prior art, checked rather than assumed: <b>CA2000</b> is the obvious candidate and does
/// not cover this. Its analysis tracks object creations and a few known factories, not an
/// arbitrary interface method's return value - measured, with a control seed proving CA2000
/// was live in the same compilation and firing on a discarded <c>new HttpClient()</c> while
/// staying silent on a discarded lease. JetBrains' <c>[MustDisposeResource]</c> covers the
/// shape but needs ReSharper or Rider, does not run in <c>dotnet build</c> or CI, and cannot
/// ship in this package.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedLeaseAnalyzer : DiagnosticAnalyzer
{
    private const string _id = DiagnosticIds.DiscardedPortMappingLease;

    private static readonly HashSet<string> _leaseMethods =
        new(StringComparer.Ordinal) { "AddPortMappingAsync", "AddAnyPortMappingAsync" };

    private static readonly DiagnosticDescriptor _rule = new(
        _id,
        title: "The port mapping's lease is discarded, so nothing will remove the mapping",
        messageFormat:
            "The lease from {0} is discarded - the mapping is never removed, and its renewal loop runs for the life of the process",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A port-mapping call returns a lease that owns the mapping on the router, a renewal "
            + "loop, and (for the PortMapper one-liner) the discovery client behind it. Dropping it "
            + "leaves the port open with nothing left to close it. Hold it in an 'await using' "
            + "variable; disposing it asynchronously deletes the mapping from the gateway.",
        helpLinkUri: DiagnosticIds.HelpLink(_id));

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(_rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (!_leaseMethods.Contains(invocation.TargetMethod.Name)
            || invocation.TargetMethod.ContainingType?.ContainingNamespace?.ToDisplayString()
                != "UPnP.Rx.PortMapping")
        {
            return;
        }

        if (IsDiscarded(invocation))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                _rule, invocation.Syntax.GetLocation(), invocation.TargetMethod.Name));
        }
    }

    /// <summary>Whether the value this invocation produces goes nowhere.</summary>
    /// <remarks>
    /// Syntactic on purpose. "Was it eventually disposed" is a dataflow question this rule
    /// deliberately does not ask: the answer would be a guess, and a guess is what the
    /// zero-false-positive budget forbids. Anything the value flows into - a variable, a
    /// return, an argument, a field - is somebody else's responsibility and is left alone.
    /// </remarks>
    private static bool IsDiscarded(IOperation operation)
    {
        // Step out through the await, which is what the call site actually writes.
        var current = operation.Parent is IAwaitOperation await ? await : operation;

        return current.Parent switch
        {
            // `await gateway.AddPortMappingAsync(...);` as a statement of its own.
            IExpressionStatementOperation => true,

            // `_ = await gateway.AddPortMappingAsync(...);`
            ISimpleAssignmentOperation { Target: IDiscardOperation } => true,

            _ => false
        };
    }
}
