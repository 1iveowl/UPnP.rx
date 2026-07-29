using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace UPnP.Rx.Analyzers;

/// <summary>Fixes for UPNPRX001 - a port-mapping lease outside the range IGD allows.</summary>
/// <remarks>
/// Two fixes rather than one, and the split is deliberate. Clamping to the maximum is
/// obviously right when the lease was too long. It is obviously <em>wrong</em> when the
/// lease was negative: the author asked for something impossible, and the two things they
/// might have meant - "the longest lease I can have" and "a permanent mapping" - are very
/// different decisions. A fix that picked one would be guessing about a firewall hole, so
/// both are offered and neither is preferred.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(LeaseDurationCodeFixProvider)), Shared]
public sealed class LeaseDurationCodeFixProvider : CodeFixProvider
{
    private const string _clampTitle = "Use the longest lease IGD allows (7 days)";
    private const string _indefiniteTitle = "Make this mapping explicitly indefinite";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticIds.LeaseDurationOutOfRange);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            if (node is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    _clampTitle,
                    _ => Task.FromResult(Replace(context.Document, root, node, "global::UPnP.Rx.PortMapping.LeaseDurations.Maximum")),
                    equivalenceKey: _clampTitle),
                diagnostic);

            // Only where it is a plausible reading of the author's intent. Someone who
            // wrote a 30-day lease meant "a long time", not "forever".
            if (diagnostic.Properties.TryGetValue(DiagnosticIds.Properties.Direction, out var direction)
                && direction == "Below")
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        _indefiniteTitle,
                        _ => Task.FromResult(Replace(context.Document, root, node, "global::UPnP.Rx.PortMapping.LeaseDurations.Indefinite")),
                        equivalenceKey: _indefiniteTitle),
                    diagnostic);
            }
        }
    }

    /// <remarks>
    /// Fully qualified with <see cref="Simplifier.Annotation"/>, so a consumer type also
    /// called <c>LeaseDurations</c> cannot capture the output; the simplifier shortens it
    /// again where that is unambiguous. <c>ParseExpression</c>, not <c>ParseName</c> - this
    /// is an expression position.
    /// </remarks>
    private static Document Replace(Document document, SyntaxNode root, SyntaxNode node, string expression)
    {
        var replacement = SyntaxFactory.ParseExpression(expression)
            .WithTriviaFrom(node)
            .WithAdditionalAnnotations(Simplifier.Annotation, Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(node, replacement));
    }
}
