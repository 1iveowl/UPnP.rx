using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// UPNPRX001. The rule's whole contract is "zero false positives, and a high
/// false-negative rate is fine", so roughly half of these assert <em>silence</em>.
/// </summary>
public class LeaseDurationAnalyzerTests
{
    private static Task VerifyAsync(string body) =>
        TestKit.VerifyAsync<LeaseDurationAnalyzer>(TestKit.InMethod(body), TestKit.PortMappingStub);

    // ---- Reported ----

    [Fact]
    public Task NegativeLease_IsReported() => VerifyAsync("""
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.FromSeconds(-5)|});
        """);

    [Fact]
    public Task LeaseAboveTheMaximum_IsReported() => VerifyAsync("""
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.FromDays(30)|});
        """);

    [Fact]
    public Task OneSecondOverTheMaximum_IsReported() => VerifyAsync("""
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.FromSeconds(604801)|});
        """);

    [Fact]
    public Task AddAnyPortMapping_IsCoveredToo() => VerifyAsync("""
                await gateway.AddAnyPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.FromHours(-1)|});
        """);

    [Fact]
    public Task TimeSpanMinValue_IsReported() => VerifyAsync("""
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.MinValue|});
        """);

    [Fact]
    public Task ConstructorForm_IsRead() => VerifyAsync("""
                // new TimeSpan(days, hours, minutes, seconds) = 8 days, over the maximum.
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:new TimeSpan(8, 0, 0, 0)|});
        """);

    [Fact]
    public Task NamedArgument_IsFoundByParameterNameNotPosition() => VerifyAsync("""
                await gateway.AddPortMappingAsync(
                    lease: {|UPNPRX001:TimeSpan.FromSeconds(-1)|},
                    externalPort: 80, internalPort: 80, protocol: Protocol.Tcp, description: "x");
        """);

    [Fact]
    public Task PortMapperOneLiner_IsCovered() => TestKit.VerifyAsync<LeaseDurationAnalyzer>("""
        using System;
        using System.Threading.Tasks;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            async Task Run() =>
                await PortMapper.AddPortMappingAsync(80, 80, Protocol.Tcp, "x",
                    {|UPNPRX001:TimeSpan.FromDays(-1)|});
        }
        """, TestKit.PortMappingStub);

    [Fact]
    public Task ObjectInitializer_IsCovered() => TestKit.VerifyAsync<LeaseDurationAnalyzer>("""
        using System;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            PortMappingEntry Make() => new()
            {
                ExternalPort = 80,
                InternalPort = 80,
                Protocol = Protocol.Tcp,
                LeaseDuration = {|UPNPRX001:TimeSpan.FromSeconds(-30)|}
            };
        }
        """, TestKit.PortMappingStub);

    [Fact]
    public Task WithExpression_IsCovered() => TestKit.VerifyAsync<LeaseDurationAnalyzer>("""
        using System;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            PortMappingEntry Change(PortMappingEntry entry) =>
                entry with { LeaseDuration = {|UPNPRX001:TimeSpan.FromDays(365)|} };
        }
        """, TestKit.PortMappingStub);

    // ---- Silence: the false-positive budget ----

    [Fact]
    public Task ALeaseInsideTheRange_IsSilent() => VerifyAsync("""
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
        """);

    [Fact]
    public Task Zero_IsSilent_BecauseItMeansIndefinite() => VerifyAsync("""
                // The documented opt-out, not a mistake.
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.Zero);
        """);

    [Fact]
    public Task ExactlyTheMaximum_IsSilent() => VerifyAsync("""
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromSeconds(604800));
        """);

    [Fact]
    public Task LeaseDurationsMaximum_IsSilent() => VerifyAsync("""
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", LeaseDurations.Maximum);
        """);

    [Fact]
    public Task AComputedLease_IsSilent() => VerifyAsync("""
                // Source cannot see it, so the rule says nothing. This is the accepted
                // false-negative half of the budget, asserted rather than assumed - the
                // run-time guard is what covers this case.
                var minutes = DateTime.Now.Minute;
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromMinutes(-minutes));
        """);

    [Fact]
    public Task AParameterisedLease_IsSilent() => TestKit.VerifyAsync<LeaseDurationAnalyzer>("""
        using System;
        using System.Threading.Tasks;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            async Task Run(IInternetGateway gateway, TimeSpan lease) =>
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", lease);
        }
        """, TestKit.PortMappingStub);

    [Fact]
    public Task ASameNamedMethodInAnotherNamespace_IsSilent() => TestKit.VerifyAsync<LeaseDurationAnalyzer>("""
        using System;
        using System.Threading.Tasks;
        using SomeoneElse.PortMapping;

        class Consumer
        {
            // Same method name, same parameter name, same bad value - different library.
            // The rule binds on the symbol, not on spelling.
            async Task Run(IInternetGateway gateway) =>
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromDays(30));
        }
        """, TestKit.LookalikeStub);

    [Fact]
    public Task ASameNamedPropertyInAnotherNamespace_IsSilent() => TestKit.VerifyAsync<LeaseDurationAnalyzer>("""
        using System;
        using SomeoneElse.PortMapping;

        class Consumer
        {
            PortMappingEntry Make() => new() { LeaseDuration = TimeSpan.FromDays(30) };
        }
        """, TestKit.LookalikeStub);

    [Fact]
    public Task AnUnrelatedTimeSpanArgument_IsSilent() => TestKit.VerifyAsync<LeaseDurationAnalyzer>("""
        using System;

        class Consumer
        {
            static void Wait(TimeSpan lease) { }

            // Right parameter name, no relationship to port mapping.
            void Run() => Wait(TimeSpan.FromDays(-30));
        }
        """);
}
