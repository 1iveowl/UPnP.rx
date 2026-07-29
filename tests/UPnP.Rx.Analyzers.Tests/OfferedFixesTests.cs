using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// Which fixes get offered, as opposed to what they produce.
/// </summary>
/// <remarks>
/// Written because the mutation battery found the gap: making the "explicitly indefinite"
/// fix available for over-long leases as well broke <em>no</em> test, so the deliberate
/// decision to withhold it was untested. Someone who wrote a 30-day lease meant "a long
/// time"; offering them "forever" as a one-click fix on a firewall hole is not a
/// suggestion this library should make.
/// </remarks>
public class OfferedFixesTests
{
    [Fact]
    public async Task NegativeLease_OffersBothClampAndIndefinite()
    {
        var titles = await OfferedFixTitlesAsync("TimeSpan.FromSeconds(-5)");

        Assert.Equal(2, titles.Length);
        Assert.Contains(titles, t => t.Contains("longest lease", StringComparison.Ordinal));
        Assert.Contains(titles, t => t.Contains("indefinite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverLongLease_OffersOnlyTheClamp()
    {
        var titles = await OfferedFixTitlesAsync("TimeSpan.FromDays(30)");

        var only = Assert.Single(titles);
        Assert.Contains("longest lease", only, StringComparison.Ordinal);
    }

    /// <summary>Compiles a snippet, runs the analyzer, and collects the fix titles offered for it.</summary>
    private static async Task<ImmutableArray<string>> OfferedFixTitlesAsync(string leaseExpression)
    {
        var source = $$"""
            using System;
            using System.Threading.Tasks;
            using UPnP.Rx.PortMapping;

            class Consumer
            {
                async Task Run(IInternetGateway gateway) =>
                    await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", {{leaseExpression}});
            }
            """;

        using var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();

        var project = workspace
            .AddProject("Fixtures", LanguageNames.CSharp)
            // The same reference set the verifier-based tests compile against, resolved
            // through the testing framework rather than hard-coded.
            .AddMetadataReferences(await Microsoft.CodeAnalysis.Testing.ReferenceAssemblies.Net.Net90
                .ResolveAsync(LanguageNames.CSharp, Xunit.TestContext.Current.CancellationToken))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        project = project.AddDocument("Stub.cs", SourceText.From(TestKit.PortMappingStub)).Project;
        var document = project.AddDocument("Consumer.cs", SourceText.From(source));

        var compilation = await document.Project.GetCompilationAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(compilation);

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LeaseDurationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(Xunit.TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == DiagnosticIds.LeaseDurationOutOfRange);

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            Xunit.TestContext.Current.CancellationToken);

        await new LeaseDurationCodeFixProvider().RegisterCodeFixesAsync(context);

        return [.. actions.Select(a => a.Title)];
    }
}
