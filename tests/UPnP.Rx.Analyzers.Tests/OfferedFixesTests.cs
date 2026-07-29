using System.Collections.Immutable;
using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// Which UPNPRX001 fixes get offered, as opposed to what they produce.
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
        var titles = await OfferedTitlesAsync("TimeSpan.FromSeconds(-5)");

        Assert.Equal(2, titles.Length);
        Assert.Contains(titles, t => t.Contains("longest lease", StringComparison.Ordinal));
        Assert.Contains(titles, t => t.Contains("indefinite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverLongLease_OffersOnlyTheClamp()
    {
        var titles = await OfferedTitlesAsync("TimeSpan.FromDays(30)");

        var only = Assert.Single(titles);
        Assert.Contains("longest lease", only, StringComparison.Ordinal);
    }

    private static Task<ImmutableArray<string>> OfferedTitlesAsync(string leaseExpression) =>
        CodeFixProbe.OfferedTitlesAsync<LeaseDurationAnalyzer, LeaseDurationCodeFixProvider>(
            $$"""
            using System;
            using System.Threading.Tasks;
            using UPnP.Rx.PortMapping;

            class Consumer
            {
                async Task Run(IInternetGateway gateway) =>
                    await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", {{leaseExpression}});
            }
            """,
            DiagnosticIds.LeaseDurationOutOfRange,
            TestKit.PortMappingStub);
}
