using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// Every rule's <c>helpLinkUri</c> must land on a heading that exists.
/// </summary>
/// <remarks>
/// <para>
/// A help link is the one part of a diagnostic a reader follows when they do not already
/// understand it, and a dead one is worse than none: it says the documentation exists and
/// then does not deliver it. Nothing in a build catches that, so it is asserted here.
/// </para>
/// <para>
/// The anchors are explicit <c>&lt;a id="..."&gt;</c> elements rather than headings, because
/// a GitHub heading slug is the whole heading text - <c>### UPNPRX001 - a lease outside…</c>
/// anchors as <c>#upnprx001---a-lease-outside…</c>, not <c>#upnprx001</c>. Explicit anchors
/// let the heading read like prose while the link stays exactly the ID.
/// </para>
/// </remarks>
public class HelpLinkTests
{
    [Fact]
    public void EveryRulesHelpAnchorExistsInTheReadme()
    {
        var readme = Readme();
        var anchors = Regex
            .Matches(readme, """<a\s+id="(?<id>[^"]+)"\s*>""", RegexOptions.IgnoreCase)
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(Ids(), id =>
        {
            var anchor = id.ToLowerInvariant();

            Assert.True(
                anchors.Contains(anchor),
                $"{id} links to #{anchor}, which no <a id=\"...\"> in README.md provides. "
                + $"Anchors found: {string.Join(", ", anchors.OrderBy(a => a, StringComparer.Ordinal))}");
        });
    }

    [Fact]
    public void EveryRuleIsAlsoNamedInTheReadmeProse()
    {
        // An anchor with nothing under it satisfies the link and helps nobody. This asserts
        // the ID is actually written out where a reader would land.
        var readme = Readme();

        Assert.All(Ids(), id => Assert.Contains(id, readme, StringComparison.Ordinal));
    }

    [Fact]
    public void TheReadmeDoesNotClaimExcludeAssetsTurnRulesOff()
    {
        // Measured three times in three repositories: ExcludeAssets="analyzers" suppresses
        // nothing. Documenting it as an off-switch would send people to a setting that
        // silently does not work, so the README must not present it as one.
        var readme = Readme();
        var mentions = readme.Split('\n')
            .Where(line => line.Contains("ExcludeAssets", StringComparison.Ordinal))
            .ToList();

        Assert.All(mentions, line => Assert.Contains("not", line, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Ids() =>
        typeof(DiagnosticIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private static string Readme()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UPnP.Rx.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine(directory.FullName, "README.md"));
    }
}
