using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace UPnP.Rx.Analyzers;

/// <summary>Fix for UPNPRX003 - hold the lease in an <c>await using</c> variable.</summary>
/// <remarks>
/// <c>await using</c> rather than <c>using</c>, because the two are not equivalent here:
/// the async path deletes the mapping from the gateway, and the sync one only stops
/// renewing and lets it lapse. Offering the sync form would fix the leak and quietly keep
/// the port open until the lease expired - or forever, for an indefinite one.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DiscardedLeaseCodeFixProvider)), Shared]
public sealed class DiscardedLeaseCodeFixProvider : CodeFixProvider
{
    private const string _title = "Hold the lease in an 'await using' variable";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticIds.DiscardedPortMappingLease);

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

            // Only the statement forms are fixable. `_ = await …` is reported but left
            // alone: rewriting a discard means guessing that the author wanted the value
            // after all, and they wrote `_` on purpose.
            if (node?.FirstAncestorOrSelf<ExpressionStatementSyntax>() is not { } statement
                || statement.Expression is not AwaitExpressionSyntax)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    _title,
                    ct => Task.FromResult(Rewrite(context.Document, root, statement, ct)),
                    equivalenceKey: _title),
                diagnostic);
        }
    }

    private static Document Rewrite(
        Document document, SyntaxNode root, ExpressionStatementSyntax statement, CancellationToken ct)
    {
        var declaration = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(NameFor(statement)))
                            .WithInitializer(SyntaxFactory.EqualsValueClause(statement.Expression)))))
            .WithAwaitKeyword(SyntaxFactory.Token(SyntaxKind.AwaitKeyword))
            .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword))
            .WithTriviaFrom(statement)
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(statement, declaration));
    }

    /// <summary>
    /// A name for the new variable that does not collide with one already in the block.
    /// </summary>
    /// <remarks>
    /// Syntactic rather than semantic, deliberately: this only has to avoid a name the
    /// enclosing block already declares, and asking the semantic model would make the fix
    /// async for no additional safety.
    /// </remarks>
    private static string NameFor(SyntaxNode statement)
    {
        var taken = statement.Ancestors()
            .OfType<BlockSyntax>()
            .SelectMany(block => block.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            .Select(declarator => declarator.Identifier.ValueText)
            .ToImmutableHashSet();

        if (!taken.Contains("lease"))
        {
            return "lease";
        }

        for (var i = 2; ; i++)
        {
            var candidate = "lease" + i;

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
