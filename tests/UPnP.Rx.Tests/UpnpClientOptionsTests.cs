using SSDP.UPnP.PCL.Model;
using Xunit;

namespace UPnP.Rx.Tests;

/// <summary>
/// The options record's ranges. Each guard is isolated - every other property is left
/// at its valid default - so a test cannot pass because a neighbouring guard threw
/// first, which is exactly how a sibling repo ended up with tests that could not fail.
/// The <c>UPNPRX002</c> analyzer reports the same mistakes at build time for literals;
/// these cover everything computed.
/// </summary>
public class UpnpClientOptionsTests
{
    public static TheoryData<int> NonPositiveSeconds => new(0, -1, -3600);

    [Theory]
    [MemberData(nameof(NonPositiveSeconds))]
    public void DescriptionTimeout_MustBePositive(int seconds)
    {
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UpnpClientOptions { DescriptionTimeout = TimeSpan.FromSeconds(seconds) });

        Assert.Contains(nameof(UpnpClientOptions.DescriptionTimeout), thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonPositiveSeconds))]
    public void ActionTimeout_MustBePositive(int seconds)
    {
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UpnpClientOptions { ActionTimeout = TimeSpan.FromSeconds(seconds) });

        Assert.Contains(nameof(UpnpClientOptions.ActionTimeout), thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonPositiveSeconds))]
    public void RosterExpiryFallback_MustBePositive(int seconds)
    {
        // A non-positive fallback would expire every device that announces no usable
        // max-age the instant it arrives - the roster would report arrivals and
        // departures forever and never hold anything.
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UpnpClientOptions { RosterExpiryFallback = TimeSpan.FromSeconds(seconds) });

        Assert.Contains(nameof(UpnpClientOptions.RosterExpiryFallback), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EventSubscriptionTimeout_BelowOneSecond_IsRefused()
    {
        // GENA carries it as whole seconds (UDA 2.0 clause 4.1.2). 500 ms composed
        // "TIMEOUT: Second-0" and a negative value composed "Second--5" - both
        // malformed, both sent without complaint.
        foreach (var bad in new[] { TimeSpan.FromMilliseconds(500), TimeSpan.Zero, TimeSpan.FromSeconds(-5) })
        {
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UpnpClientOptions { EventSubscriptionTimeout = bad });

            Assert.Contains("at least one second", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            TimeSpan.FromSeconds(1),
            new UpnpClientOptions { EventSubscriptionTimeout = TimeSpan.FromSeconds(1) }.EventSubscriptionTimeout);
    }

    [Fact]
    public void WithExpressions_AreGuardedToo()
    {
        // `with` runs the init accessors again, so the guard has to hold there as well -
        // and `with` is how the docs tell consumers to derive variants.
        var options = new UpnpClientOptions();

        // UPNPRX002 reports this literal - and reports it inside a `with`, which is half of
        // what this test exists to check. Suppressed because asserting the run-time guard
        // requires actually writing the bad value.
#pragma warning disable UPNPRX002 // Deliberate: asserting the init accessor rejects it.
        Assert.Throws<ArgumentOutOfRangeException>(() => options with { ActionTimeout = TimeSpan.Zero });
#pragma warning restore UPNPRX002
        Assert.Equal(TimeSpan.FromSeconds(30), options.ActionTimeout);   // the original is untouched
    }

    [Fact]
    public void Defaults_AreAllInsideTheirOwnRanges()
    {
        // A default that its own guard would reject is a construction that throws on
        // `new UpnpClientOptions()` - worth one assertion rather than a support thread.
        var options = new UpnpClientOptions();

        Assert.True(options.DescriptionTimeout > TimeSpan.Zero);
        Assert.True(options.ActionTimeout > TimeSpan.Zero);
        Assert.True(options.RosterExpiryFallback > TimeSpan.Zero);
        Assert.True(options.EventSubscriptionTimeout >= TimeSpan.FromSeconds(1));
        Assert.Equal(3, options.DefaultMx.Seconds);
        Assert.Equal(0, options.EventCallbackPort);   // 0 == ephemeral, deliberately legal
    }

    [Fact]
    public void EventCallbackPort_RangeIsTheTypeRatherThanAGuard()
    {
        // ushort is the whole validation: a TCP port outside 0-65535 cannot be written.
        // That is the API change deleting a rule, and this test is what says so.
        Assert.Equal(ushort.MaxValue, new UpnpClientOptions { EventCallbackPort = ushort.MaxValue }.EventCallbackPort);
        Assert.Equal(typeof(ushort), typeof(UpnpClientOptions).GetProperty(nameof(UpnpClientOptions.EventCallbackPort))!.PropertyType);
    }

    [Fact]
    public void DefaultMx_FloorIsTheTypeRatherThanAGuard()
    {
        Assert.Equal(typeof(MxSeconds), typeof(UpnpClientOptions).GetProperty(nameof(UpnpClientOptions.DefaultMx))!.PropertyType);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpnpClientOptions { DefaultMx = new MxSeconds(0) });
    }
}
