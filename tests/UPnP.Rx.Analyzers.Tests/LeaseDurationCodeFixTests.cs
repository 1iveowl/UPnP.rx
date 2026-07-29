using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>The UPNPRX001 fixes, including that the second one is offered only where it makes sense.</summary>
public class LeaseDurationCodeFixTests
{
    private static Task VerifyAsync(string body, string fixedBody, int? index = null) =>
        TestKit.VerifyFixAsync<LeaseDurationAnalyzer, LeaseDurationCodeFixProvider>(
            TestKit.InMethod(body), TestKit.InMethod(fixedBody), index, TestKit.PortMappingStub);

    [Fact]
    public Task TooLong_IsClampedToTheMaximum() => VerifyAsync(
        """
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.FromDays(30)|});
        """,
        """
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    LeaseDurations.Maximum);
        """);

    [Fact]
    public Task Negative_ClampToMaximum_IsTheFirstFix() => VerifyAsync(
        """
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.FromSeconds(-5)|});
        """,
        """
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    LeaseDurations.Maximum);
        """,
        index: 0);

    [Fact]
    public Task Negative_ExplicitlyIndefinite_IsTheSecondFix() => VerifyAsync(
        """
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.FromSeconds(-5)|});
        """,
        """
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    LeaseDurations.Indefinite);
        """,
        index: 1);

    [Fact]
    public Task ObjectInitializer_IsFixedToo() =>
        TestKit.VerifyFixAsync<LeaseDurationAnalyzer, LeaseDurationCodeFixProvider>(
            """
            using System;
            using UPnP.Rx.PortMapping;

            class Consumer
            {
                PortMappingEntry Make() => new()
                {
                    ExternalPort = 80,
                    InternalPort = 80,
                    Protocol = Protocol.Tcp,
                    LeaseDuration = {|UPNPRX001:TimeSpan.FromDays(365)|}
                };
            }
            """,
            """
            using System;
            using UPnP.Rx.PortMapping;

            class Consumer
            {
                PortMappingEntry Make() => new()
                {
                    ExternalPort = 80,
                    InternalPort = 80,
                    Protocol = Protocol.Tcp,
                    LeaseDuration = LeaseDurations.Maximum
                };
            }
            """,
            codeActionIndex: null,
            TestKit.PortMappingStub);

    [Fact]
    public Task TheFixSurvivesAConsumerTypeOfTheSameName() =>
        // The failure this guards against is silent and total: emitting a bare
        // "LeaseDurations.Maximum" here would bind to the consumer's own type, compile
        // cleanly, and mean one day instead of seven. The fix emits
        // global::UPnP.Rx.PortMapping.LeaseDurations with Simplifier.Annotation, and the
        // simplifier shortens it only as far as stays unambiguous - here that is the
        // namespace-qualified form, with global:: dropped but the namespace kept.
        //
        // Asserting the exact text is the point: fixed code that bound to the wrong type
        // would still compile, so compilation alone would not catch it.
        TestKit.VerifyFixAsync<LeaseDurationAnalyzer, LeaseDurationCodeFixProvider>(
            """
            using System;
            using System.Threading.Tasks;
            using UPnP.Rx.PortMapping;

            static class LeaseDurations
            {
                public static TimeSpan Maximum => TimeSpan.FromDays(1);
            }

            class Consumer
            {
                async Task Run(IInternetGateway gateway) =>
                    await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                        {|UPNPRX001:TimeSpan.FromDays(30)|});
            }
            """,
            """
            using System;
            using System.Threading.Tasks;
            using UPnP.Rx.PortMapping;

            static class LeaseDurations
            {
                public static TimeSpan Maximum => TimeSpan.FromDays(1);
            }

            class Consumer
            {
                async Task Run(IInternetGateway gateway) =>
                    await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                        UPnP.Rx.PortMapping.LeaseDurations.Maximum);
            }
            """,
            codeActionIndex: 0,
            TestKit.PortMappingStub);
}
