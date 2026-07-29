using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>The UPNPRX003 fix, and the one shape it deliberately declines to touch.</summary>
public class DiscardedLeaseCodeFixTests
{
    private static Task VerifyAsync(string body, string fixedBody) =>
        TestKit.VerifyFixAsync<DiscardedLeaseAnalyzer, DiscardedLeaseCodeFixProvider>(
            TestKit.InMethod(body), TestKit.InMethod(fixedBody), null, TestKit.PortMappingStub);

    [Fact]
    public Task AStatement_BecomesAnAwaitUsingVariable() => VerifyAsync(
        """
                await {|UPNPRX003:gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1))|};
        """,
        """
                await using var lease = await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
        """);

    [Fact]
    public Task TheNameAvoidsOneAlreadyInTheBlock() => VerifyAsync(
        """
                var lease = "already taken";
                await {|UPNPRX003:gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1))|};
                System.Console.WriteLine(lease);
        """,
        """
                var lease = "already taken";
                await using var lease2 = await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
                System.Console.WriteLine(lease);
        """);

    [Fact]
    public async Task ADiscardIsReportedButNotFixed()
    {
        // `_ = await …` is a leak worth reporting, but rewriting it means deciding the
        // author did not mean the `_` they typed. They get the diagnostic and choose.
        var titles = await OfferedTitlesAsync(
            """_ = await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));""");

        Assert.Empty(titles);
    }

    [Fact]
    public async Task AStatementIsFixable()
    {
        // The control for the test above: same harness, a shape that IS fixed. Without
        // this, "no fixes offered" could equally mean the harness found no diagnostic.
        var titles = await OfferedTitlesAsync(
            """await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));""");

        Assert.Single(titles);
        Assert.Contains("await using", titles[0], StringComparison.Ordinal);
    }

    private static Task<System.Collections.Immutable.ImmutableArray<string>> OfferedTitlesAsync(string statement) =>
        CodeFixProbe.OfferedTitlesAsync<DiscardedLeaseAnalyzer, DiscardedLeaseCodeFixProvider>(
            $$"""
            using System;
            using System.Threading.Tasks;
            using UPnP.Rx.PortMapping;

            class Consumer
            {
                async Task Run(IInternetGateway gateway)
                {
                    {{statement}}
                }
            }
            """,
            DiagnosticIds.DiscardedPortMappingLease,
            TestKit.PortMappingStub);
}
