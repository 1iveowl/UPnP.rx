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
    public async Task ADocumentWithNoActionsGeneratesNothing()
    {
        // Leniency: a document identifying no actions is not an error, it just has nothing
        // to say. The generator reports the missing-document diagnostic instead of emitting
        // an empty wrapper whose call sites all fail to compile.
        var result = await RunAsync("<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"><actionList /></scpd>");

        Assert.Empty(result.GeneratedTrees);
        Assert.Equal(DiagnosticIds.ScpdDocumentNotFound, Assert.Single(result.Diagnostics).Id);
    }

    private static async Task VerifyAsync(string snapshotName, string scpd)
    {
        var result = await RunAsync(scpd);
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

    private static async Task<GeneratorDriverRunResult> RunAsync(string scpd)
    {
        var references = await ReferenceAssemblies.Net.Net90.ResolveAsync(LanguageNames.CSharp, Ct);

        var compilation = CSharpCompilation.Create(
            "Consumer",
            [
                CSharpSyntaxTree.ParseText(
                    """
                    using UPnP.Rx.Model;

                    namespace Consumers
                    {
                        [ScpdService("Snapshot.scpd.xml")]
                        public sealed partial class SnapshotService { }
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
                additionalTexts: [new SnapshotText("/fixtures/Snapshot.scpd.xml", scpd)])
            .RunGenerators(compilation, Ct)
            .GetRunResult();
    }

    /// <summary>The snapshots live beside the tests, not beside the binaries.</summary>
    private static string SnapshotDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UPnP.Rx.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var snapshots = Path.Combine(directory.FullName, "tests", "UPnP.Rx.Analyzers.Tests", "Snapshots");
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
