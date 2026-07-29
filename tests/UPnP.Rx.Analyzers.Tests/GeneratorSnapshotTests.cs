using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// Snapshot tests for the generator: the emitted text is compared against a checked-in
/// file, so any change to the generated shape shows up as a reviewable diff.
/// </summary>
/// <remarks>
/// <para>
/// This matters more for a generator than for ordinary code. Every generated member is
/// public API with no deprecation path - renaming one is a breaking change for everyone who
/// used it - so "the shape changed" needs to be something a reviewer sees rather than
/// something a consumer discovers.
/// </para>
/// <para>
/// <b>Hand-rolled rather than Verify</b>, which is what the plan called for: nuget.org was
/// unreachable when this was written, so the package could not be restored. The mechanism
/// is the same - compare against a committed snapshot, write the actual output beside it on
/// mismatch - and swapping in Verify later is a contained change. Recorded rather than
/// quietly substituted.
/// </para>
/// </remarks>
public class GeneratorSnapshotTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public Task AServiceWithEveryArgumentShape() => VerifyAsync(
        "EveryShape",
        """
        <?xml version="1.0"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <actionList>
            <action>
              <name>DoEverything</name>
              <argumentList>
                <argument><name>NewText</name><direction>in</direction><relatedStateVariable>Text</relatedStateVariable></argument>
                <argument><name>NewCount</name><direction>in</direction><relatedStateVariable>Count</relatedStateVariable></argument>
                <argument><name>NewFlag</name><direction>in</direction><relatedStateVariable>Flag</relatedStateVariable></argument>
                <argument><name>NewChoice</name><direction>in</direction><relatedStateVariable>Choice</relatedStateVariable></argument>
                <argument><name>NewResult</name><direction>out</direction><relatedStateVariable>Text</relatedStateVariable></argument>
              </argumentList>
            </action>
            <action>
              <name>NoArguments</name>
              <argumentList />
            </action>
          </actionList>
          <serviceStateTable>
            <stateVariable sendEvents="no"><name>Text</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no">
              <name>Count</name><dataType>ui2</dataType>
              <allowedValueRange><minimum>1</minimum><maximum>65535</maximum></allowedValueRange>
            </stateVariable>
            <stateVariable sendEvents="no"><name>Flag</name><dataType>boolean</dataType></stateVariable>
            <stateVariable sendEvents="no">
              <name>Choice</name><dataType>string</dataType>
              <allowedValueList><allowedValue>Alpha</allowedValue><allowedValue>Beta</allowedValue></allowedValueList>
            </stateVariable>
          </serviceStateTable>
        </scpd>
        """);

    [Fact]
    public Task AnAwkwardlyNamedArgument() => VerifyAsync(
        "AwkwardNames",
        // Real documents contain names that are not C# identifiers, and one that is a
        // keyword once camel-cased. Both have to come out compiling.
        """
        <?xml version="1.0"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <actionList>
            <action>
              <name>Awkward</name>
              <argumentList>
                <argument><name>New-Hyphenated</name><direction>in</direction><relatedStateVariable>Text</relatedStateVariable></argument>
                <argument><name>Class</name><direction>in</direction><relatedStateVariable>Text</relatedStateVariable></argument>
                <argument><name>9Leading</name><direction>in</direction><relatedStateVariable>Text</relatedStateVariable></argument>
              </argumentList>
            </action>
          </actionList>
          <serviceStateTable>
            <stateVariable sendEvents="no"><name>Text</name><dataType>string</dataType></stateVariable>
          </serviceStateTable>
        </scpd>
        """);

    [Fact]
    public async Task TheShippedWanIpConnectionWrapper()
    {
        // The one snapshot taken from the REAL checked-in document rather than a synthetic
        // one, so the wrapper this package actually ships shows up in a diff when it moves.
        //
        // This is the answer to "should generated code be checked in?": it effectively is,
        // here, where a test verifies it still matches. Emitting it into a Generated/ folder
        // in the project instead would either be compiled twice (the SDK globs it AND the
        // generator emits it - CS0102 on every type) or excluded from compilation, in which
        // case it is unverified documentation that drifts silently. A snapshot is the same
        // artefact with a check attached.
        //
        // Signatures are separately guarded by the public-API ledger; what this adds is the
        // BODIES - the range guards, the wire composition, the result construction.
        var scpd = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "UPnP.Rx", "Scpd", "WANIPConnection1.scpd.xml"), Ct);

        // The real document name and type name, so this snapshot is the shipped file rather
        // than a lookalike - only the namespace differs, which the generator takes from the
        // declaring class and the test cannot place in UPnP.Rx.PortMapping.
        await VerifyAsync("WanIpConnection", scpd, "WANIPConnection1.scpd.xml", "WanIpConnection");
    }

    [Fact]
    public async Task ADuplicateActionNameIsTakenOnce()
    {
        // Found in the 6.0.0 review: the reader deduped state variables but not actions, so
        // a document declaring one twice emitted two methods with a single signature - and
        // the CONSUMER's build broke on code they never wrote. First declaration wins, which
        // is what the state-variable table beside it had always done.
        var result = await RunAsync("""
            <?xml version="1.0"?>
            <scpd xmlns="urn:schemas-upnp-org:service-1-0">
              <actionList>
                <action><name>Twice</name><argumentList /></action>
                <action><name>Twice</name><argumentList /></action>
                <action><name>TWICE</name><argumentList /></action>
              </actionList>
              <serviceStateTable />
            </scpd>
            """);

        var generated = Assert.Single(result.GeneratedTrees).ToString();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(generated, @"public async Task TwiceAsync\("));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ADocumentWithNoActionsGeneratesNothing()
    {
        // Leniency: a document identifying no actions is not an error, it just has nothing
        // to say. The generator reports the missing-document diagnostic instead of emitting
        // an empty wrapper whose call sites all fail to compile.
        var result = await RunAsync("<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"><actionList /></scpd>");

        Assert.Empty(result.GeneratedTrees);
        Assert.Equal(DiagnosticIds.ScpdDocumentNotFound, Assert.Single(result.Diagnostics).Id);
    }

    private static async Task VerifyAsync(
        string snapshotName, string scpd, string documentName = "Snapshot.scpd.xml", string typeName = "SnapshotService")
    {
        var result = await RunAsync(scpd, documentName, typeName);
        var actual = Assert.Single(result.GeneratedTrees).ToString().Replace("\r\n", "\n");

        var directory = SnapshotDirectory();
        var expectedPath = Path.Combine(directory, $"{snapshotName}.verified.cs");
        var receivedPath = Path.Combine(directory, $"{snapshotName}.received.cs");

        if (!File.Exists(expectedPath))
        {
            await File.WriteAllTextAsync(receivedPath, actual, Ct);
            Assert.Fail(
                $"No snapshot at {expectedPath}. The generated output was written to {receivedPath}; "
                + "review it and rename it to .verified.cs to accept.");
        }

        var expected = (await File.ReadAllTextAsync(expectedPath, Ct)).Replace("\r\n", "\n");

        if (expected != actual)
        {
            await File.WriteAllTextAsync(receivedPath, actual, Ct);
        }
        else if (File.Exists(receivedPath))
        {
            File.Delete(receivedPath);
        }

        Assert.Equal(expected, actual);
    }

    private static async Task<GeneratorDriverRunResult> RunAsync(
        string scpd, string documentName = "Snapshot.scpd.xml", string typeName = "SnapshotService")
    {
        var references = await ReferenceAssemblies.Net.Net90.ResolveAsync(LanguageNames.CSharp, Ct);

        var compilation = CSharpCompilation.Create(
            "Consumer",
            [
                CSharpSyntaxTree.ParseText(
                    $$"""
                    using UPnP.Rx.Model;

                    namespace Consumers
                    {
                        [ScpdService("{{documentName}}")]
                        public sealed partial class {{typeName}} { }
                    }
                    """,
                    cancellationToken: Ct),
                CSharpSyntaxTree.ParseText(GeneratorCacheabilityTests.AttributeStub, cancellationToken: Ct)
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return CSharpGeneratorDriver
            .Create(
                generators: [new ScpdServiceGenerator().AsSourceGenerator()],
                additionalTexts: [new SnapshotText("/fixtures/" + documentName, scpd)])
            .RunGenerators(compilation, Ct)
            .GetRunResult();
    }

    /// <summary>The repository root, found by walking up from the test binaries.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UPnP.Rx.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    /// <summary>The snapshots live beside the tests, not beside the binaries.</summary>
    private static string SnapshotDirectory()
    {
        var snapshots = Path.Combine(RepoRoot(), "tests", "UPnP.Rx.Analyzers.Tests", "Snapshots");
        Directory.CreateDirectory(snapshots);

        return snapshots;
    }

    private sealed class SnapshotText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) =>
            Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }
}
