using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// Asks which fixes a provider <em>offers</em>, as opposed to what they produce.
/// </summary>
/// <remarks>
/// The verifier-based harness answers "does applying fix N give this text", which cannot
/// express "fix N is not offered at all" - and that is a real design decision for two of
/// our providers: UPNPRX001 withholds "make it indefinite" for over-long leases, and
/// UPNPRX003 declines to rewrite a discard. Both were untested until a mutation proved
/// nothing failed when they were removed.
/// </remarks>
internal static class CodeFixProbe
{
    /// <summary>The titles of the fixes offered for the first diagnostic of the given ID.</summary>
    /// <param name="source">The code under test.</param>
    /// <param name="diagnosticId">The rule whose diagnostic the fixes are requested for.</param>
    /// <param name="additionalSources">Stub sources the code compiles against.</param>
    public static async Task<ImmutableArray<string>> OfferedTitlesAsync<TAnalyzer, TCodeFix>(
        string source,
        string diagnosticId,
        params string[] additionalSources)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        using var workspace = new AdhocWorkspace();

        var project = workspace
            .AddProject("Fixtures", LanguageNames.CSharp)
            // The same reference set the verifier-based tests compile against, resolved
            // through the testing framework rather than hard-coded.
            .AddMetadataReferences(await ReferenceAssemblies.Net.Net90.ResolveAsync(LanguageNames.CSharp, ct))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        for (var i = 0; i < additionalSources.Length; i++)
        {
            project = project.AddDocument($"Stub{i}.cs", SourceText.From(additionalSources[i])).Project;
        }

        var document = project.AddDocument("Consumer.cs", SourceText.From(source));

        var compilation = await document.Project.GetCompilationAsync(ct);
        Assert.NotNull(compilation);

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(ct);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == diagnosticId);

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        await new TCodeFix().RegisterCodeFixesAsync(
            new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), ct));

        return [.. actions.Select(a => a.Title)];
    }
}
