using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using UPnP.Rx.Analyzers;
using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// The contracts that hold for every rule, checked once rather than per rule. These are
/// the things that silently rot: a help anchor that no longer matches a README heading, a
/// severity quietly demoted, an ID that appears in one place and not the other.
/// </summary>
public class InfrastructureTests
{
    /// <summary>
    /// Every analyzer this assembly ships, discovered rather than listed by hand.
    /// Iterated with <c>Assert.All</c> rather than driven as a Theory: xUnit fails a
    /// Theory with no data, and "no rules yet" is a legitimate state between phases.
    /// </summary>
    private static ImmutableArray<DiagnosticDescriptor> Descriptors()
    {
        var assembly = typeof(DiagnosticIds).Assembly;

        var fromAnalyzers = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t))
            .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)
            .SelectMany(a => a.SupportedDiagnostics);

        // Generators report diagnostics too, and have no SupportedDiagnostics to enumerate,
        // so their descriptors are found as static fields. Without this the contracts below
        // would silently stop covering UPNPRXGEN001.
        var fromGenerators = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(t))
            .SelectMany(t => t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
            .Select(f => (DiagnosticDescriptor)f.GetValue(null)!);

        return [.. fromAnalyzers.Concat(fromGenerators)];
    }

    [Fact]
    public void EveryDeclaredIdIsImplementedByAnAnalyzer()
    {
        // Catches the half-landed rule: an ID reserved in DiagnosticIds, documented in the
        // README, and never actually reported by anything.
        var declared = typeof(DiagnosticIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var implemented = Descriptors().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.OrderBy(x => x, StringComparer.Ordinal), implemented.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryRuleIsAWarning() => Assert.All(Descriptors(), descriptor =>
    {
        // Settled policy: not Error (that forces pragmas into correct code), not
        // Info/Hidden (an invisible rule protects nobody). A demotion should be a
        // deliberate, visible act rather than a quiet edit.
        Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
        Assert.True(descriptor.IsEnabledByDefault, $"{descriptor.Id} must be on by default.");
    });

    [Fact]
    public void EveryRuleHasItsOwnStableHelpAnchor() => Assert.All(Descriptors(), descriptor =>
    {
        // The anchor contract with the README (P9): one heading per rule, named for the
        // lower-cased ID. A copy-pasted descriptor pointing at the wrong anchor is the
        // failure this catches.
        Assert.Equal(DiagnosticIds.HelpLink(descriptor.Id), descriptor.HelpLinkUri);
        Assert.EndsWith("#" + descriptor.Id.ToLowerInvariant(), descriptor.HelpLinkUri, StringComparison.Ordinal);
    });

    [Fact]
    public void EveryRuleSaysSomethingUseful() => Assert.All(Descriptors(), descriptor =>
    {
        Assert.StartsWith("UPNPRX", descriptor.Id, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Title.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description.ToString()));

        // A message that never names the offending value is a message that sends the
        // reader back to the code to find out what happened.
        Assert.Contains("{0}", descriptor.MessageFormat.ToString(), StringComparison.Ordinal);
    });

    [Fact]
    public void ReleaseTrackingFilesAreValidForWhicheverStateTheyAreIn()
    {
        // Two different rules apply depending on whether anything has shipped, and the
        // empty one is the trap: an empty Shipped.md must contain comments ONLY, because a
        // bare "### Shipped" header is invalid and fails with RS2007 - a confusing analyzer
        // error in an otherwise unrelated build.
        //
        // Once a release IS recorded, table rows are exactly what belongs there. This test
        // originally only knew the empty case and started failing the moment 6.0 rolled;
        // it now asserts both, so it keeps its teeth in either state.
        var lines = ReadTrackingFile("AnalyzerReleases.Shipped.md")
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (!lines.Any(line => line.StartsWith("## Release", StringComparison.Ordinal)))
        {
            Assert.All(lines, line => Assert.StartsWith(";", line, StringComparison.Ordinal));
            return;
        }

        // Every rule this assembly reports must appear under some release heading, or the
        // next roll silently drops it and RS2000 only complains about what is left.
        var text = string.Join("\n", lines);

        Assert.All(
            Descriptors().Select(d => d.Id).Where(id => id.StartsWith("UPNPRX0", StringComparison.Ordinal)),
            id => Assert.Contains(id, text, StringComparison.Ordinal));

        Assert.Contains("Rule ID | Category | Severity | Notes", text, StringComparison.Ordinal);
    }

    private static string[] ReadTrackingFile(string name)
    {
        // Walk up to the repo root rather than hard-coding a relative depth from bin/.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UPnP.Rx.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllLines(Path.Combine(directory.FullName, "src", "UPnP.Rx.Analyzers", name));
    }
}
