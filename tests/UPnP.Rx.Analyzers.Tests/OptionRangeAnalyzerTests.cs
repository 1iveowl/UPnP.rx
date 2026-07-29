using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// UPNPRX002. As with UPNPRX001, roughly half of these assert silence - and here the
/// silence cases carry more weight, because the tempting version of this rule (report
/// anything that "looks too short") is precisely the one that would get suppressed.
/// </summary>
public class OptionRangeAnalyzerTests
{
    private static Task VerifyAsync(string initializer) =>
        TestKit.VerifyAsync<OptionRangeAnalyzer>($$"""
            using System;
            using UPnP.Rx;

            class Consumer
            {
                UpnpClientOptions Make() => new()
                {
            {{initializer}}
                };
            }
            """, TestKit.OptionsStub);

    // ---- Reported: values that cannot be right anywhere ----

    [Fact]
    public Task ZeroActionTimeout_IsReported() =>
        VerifyAsync("""        ActionTimeout = {|UPNPRX002:TimeSpan.Zero|}""");

    [Fact]
    public Task NegativeActionTimeout_IsReported() =>
        VerifyAsync("""        ActionTimeout = {|UPNPRX002:TimeSpan.FromSeconds(-1)|}""");

    [Fact]
    public Task ZeroDescriptionTimeout_IsReported() =>
        VerifyAsync("""        DescriptionTimeout = {|UPNPRX002:TimeSpan.Zero|}""");

    [Fact]
    public Task ZeroRosterExpiryFallback_IsReported() =>
        VerifyAsync("""        RosterExpiryFallback = {|UPNPRX002:TimeSpan.Zero|}""");

    [Fact]
    public Task SubSecondEventSubscriptionTimeout_IsReported() =>
        // GENA carries whole seconds: 500 ms composes "TIMEOUT: Second-0".
        VerifyAsync("""        EventSubscriptionTimeout = {|UPNPRX002:TimeSpan.FromMilliseconds(500)|}""");

    [Fact]
    public Task ZeroEventSubscriptionTimeout_IsReported() =>
        VerifyAsync("""        EventSubscriptionTimeout = {|UPNPRX002:TimeSpan.Zero|}""");

    [Fact]
    public Task SeveralBadValuesAreReportedIndependently() =>
        VerifyAsync("""
                    ActionTimeout = {|UPNPRX002:TimeSpan.Zero|},
                    DescriptionTimeout = {|UPNPRX002:TimeSpan.FromSeconds(-5)|}
        """);

    [Fact]
    public Task WithExpression_IsCovered() => TestKit.VerifyAsync<OptionRangeAnalyzer>("""
        using System;
        using UPnP.Rx;

        class Consumer
        {
            UpnpClientOptions Change(UpnpClientOptions options) =>
                options with { ActionTimeout = {|UPNPRX002:TimeSpan.Zero|} };
        }
        """, TestKit.OptionsStub);

    // ---- Silence ----

    [Fact]
    public Task AShortButPositiveTimeout_IsSilent() =>
        // 100 ms may be exactly right on a fast LAN. Reporting it is how a rule teaches
        // people to suppress the whole prefix.
        VerifyAsync("""        ActionTimeout = TimeSpan.FromMilliseconds(100)""");

    [Fact]
    public Task ExactlyOneSecondSubscriptionTimeout_IsSilent() =>
        VerifyAsync("""        EventSubscriptionTimeout = TimeSpan.FromSeconds(1)""");

    [Fact]
    public Task TheDefaults_AreSilent() =>
        VerifyAsync("""
                    DescriptionTimeout = TimeSpan.FromSeconds(30),
                    ActionTimeout = TimeSpan.FromSeconds(30),
                    RosterExpiryFallback = TimeSpan.FromMinutes(30),
                    EventSubscriptionTimeout = TimeSpan.FromMinutes(30)
        """);

    [Fact]
    public Task ZeroEventCallbackPort_IsSilent() =>
        // 0 means "bind an ephemeral port" and is the documented default. It is also not a
        // TimeSpan, so the rule should not even look at it.
        VerifyAsync("""        EventCallbackPort = 0""");

    [Fact]
    public Task AComputedTimeout_IsSilent() => TestKit.VerifyAsync<OptionRangeAnalyzer>("""
        using System;
        using UPnP.Rx;

        class Consumer
        {
            UpnpClientOptions Make(TimeSpan configured) => new() { ActionTimeout = configured };
        }
        """, TestKit.OptionsStub);

    [Fact]
    public Task ASameNamedOptionsTypeElsewhere_IsSilent() => TestKit.VerifyAsync<OptionRangeAnalyzer>("""
        using System;
        using SomeoneElse;

        class Consumer
        {
            // Same type name, same property name, same bad value - different library.
            UpnpClientOptions Make() => new() { ActionTimeout = TimeSpan.Zero };
        }
        """, TestKit.LookalikeOptionsStub);

    [Fact]
    public Task AnUnrelatedTimeSpanProperty_IsSilent() => TestKit.VerifyAsync<OptionRangeAnalyzer>("""
        using System;

        class Timings
        {
            public TimeSpan ActionTimeout { get; init; }
        }

        class Consumer
        {
            Timings Make() => new() { ActionTimeout = TimeSpan.Zero };
        }
        """);
}
