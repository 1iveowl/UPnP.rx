using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// The generator's incremental pipeline must actually be incremental.
/// </summary>
/// <remarks>
/// This is the check that is easy to skip and expensive to skip. An
/// <c>IIncrementalGenerator</c> whose pipeline carries an <c>ISymbol</c>, a
/// <c>Compilation</c> or a syntax node still produces correct output - it just recomputes
/// everything on every keystroke and keeps whole compilations alive while doing it. Nothing
/// fails; the IDE simply gets slower. So the caching is asserted rather than trusted.
/// </remarks>
public class GeneratorCacheabilityTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private const string _consumer = """
        using UPnP.Rx.Model;

        namespace Consumers
        {
            [ScpdService("Test1.scpd.xml")]
            public sealed partial class TestService { }
        }
        """;

    private const string _scpd = """
        <?xml version="1.0"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <actionList>
            <action>
              <name>GetThing</name>
              <argumentList>
                <argument><name>NewThing</name><direction>out</direction><relatedStateVariable>Thing</relatedStateVariable></argument>
              </argumentList>
            </action>
          </actionList>
          <serviceStateTable>
            <stateVariable sendEvents="no"><name>Thing</name><dataType>string</dataType></stateVariable>
          </serviceStateTable>
        </scpd>
        """;

    [Fact]
    public async Task AnUnrelatedEditReusesEveryCachedStep()
    {
        var compilation = await CompilationAsync(_consumer);
        GeneratorDriver driver = Driver();

        // First run populates the cache.
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _, Ct);

        // An edit that touches nothing the generator reads: a new, unrelated type.
        var edited = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("namespace Consumers { class Unrelated { } }", cancellationToken: Ct));

        var second = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out _, Ct);

        var steps = second.GetRunResult().Results
            .SelectMany(result => result.TrackedOutputSteps)
            .SelectMany(pair => pair.Value)
            .SelectMany(step => step.Outputs)
            .ToList();

        Assert.NotEmpty(steps);

        // Cached or Unchanged both mean "did not recompute". New means the pipeline saw a
        // difference where there is none - the signature of a non-equatable model.
        Assert.All(steps, output => Assert.Contains(
            output.Reason,
            new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged }));
    }

    [Fact]
    public async Task TheSameInputTwiceProducesTheSameOutput()
    {
        // Determinism, separately from caching: two independent runs over identical input
        // must emit identical text, or a snapshot test could never be stable.
        var first = await RunAsync();
        var second = await RunAsync();

        Assert.Equal(first, second);

        static async Task<string> RunAsync()
        {
            var result = Driver()
                .RunGenerators(await CompilationAsync(_consumer), Ct)
                .GetRunResult();

            return string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        }
    }

    [Fact]
    public async Task AMissingDocumentIsReportedRatherThanSilentlyProducingNothing()
    {
        // Without the diagnostic, a typo in the file name means no members are generated and
        // every call site fails with "no such method" - a confusing way to learn about a
        // misspelled AdditionalFiles entry.
        var compilation = await CompilationAsync(_consumer);
        var result = Driver(withDocument: false).RunGenerators(compilation, Ct).GetRunResult();

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticIds.ScpdDocumentNotFound, diagnostic.Id);
        Assert.Contains("Test1.scpd.xml", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private static CSharpGeneratorDriver Driver(bool withDocument = true) =>
        CSharpGeneratorDriver.Create(
            generators: [new ScpdServiceGenerator().AsSourceGenerator()],
            additionalTexts: withDocument
                ? [new InMemoryAdditionalText("/fixtures/Test1.scpd.xml", _scpd)]
                : [],
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

    private static async Task<CSharpCompilation> CompilationAsync(string source)
    {
        var references = await ReferenceAssemblies.Net.Net90.ResolveAsync(LanguageNames.CSharp, Ct);

        return CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: Ct), CSharpSyntaxTree.ParseText(AttributeStub, cancellationToken: Ct)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// The marker attribute, as a stub for the same reason the analyzer tests use one: a
    /// net10.0 library cannot be referenced from a test compilation.
    /// <see cref="StubGuardTests"/> holds it to the real declaration.
    /// </summary>
    internal const string AttributeStub = """
        namespace UPnP.Rx.Model
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class ScpdServiceAttribute : System.Attribute
            {
                public ScpdServiceAttribute(string scpdFileName) => ScpdFileName = scpdFileName;
                public string ScpdFileName { get; }
            }
        }
        """;

    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(text);
    }
}
