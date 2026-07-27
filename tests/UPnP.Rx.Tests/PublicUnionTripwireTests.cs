using System.Reflection;
using UPnP.Rx.Eventing;
using UPnP.Rx.Presence;
using Xunit;

namespace UPnP.Rx.Tests;

/// <summary>
/// Tripwires, not coverage. Three times during 5.0.0 a case was added to a public
/// union and a consumer's <c>switch</c> quietly absorbed it: an <c>ssdp:update</c>
/// rendered as "alive", a <see cref="SubscriptionCancelled"/> reached the browser
/// with its reason dropped, and a stream-ending echo escaped de-duplication. The
/// compiler cannot help - a type-pattern switch over a class hierarchy always needs
/// a discard arm, so exhaustiveness is unenforceable there.
/// <para>
/// These tests fail when a union grows, and say where else to look. Update the list
/// <i>and</i> the consumers named in the message; the dashboard sample cannot be
/// referenced from here (its client half is WebAssembly), so this is the only place
/// the reminder can live.
/// </para>
/// </summary>
public class PublicUnionTripwireTests
{
    private static string[] ConcreteCasesOf<TBase>() =>
        [.. typeof(TBase).Assembly
            .GetExportedTypes()
            .Where(t => typeof(TBase).IsAssignableFrom(t) && t != typeof(TBase) && !t.IsAbstract)
            .Select(t => t.Name)
            .OrderBy(name => name, StringComparer.Ordinal)];

    [Fact]
    public void UpnpEventCases_AreKnown()
    {
        string[] expected =
        [
            "GapDetected", "PropertyChange", "RenewalFailed", "Resubscribed",
            "Subscribed", "SubscriptionCancelled", "SubscriptionRefused"
        ];

        Assert.Equal(expected, ConcreteCasesOf<UpnpEvent>());
    }

    [Fact]
    public void RosterChangeCases_AreKnown()
    {
        string[] expected =
        [
            "DeviceAppeared", "DeviceExpired", "DeviceLeft", "DeviceRebooted", "DeviceUpdated"
        ];

        Assert.Equal(expected, ConcreteCasesOf<RosterChange>());
    }

    [Fact]
    public void AnnouncementKinds_AreKnown()
    {
        string[] expected = ["Alive", "ByeBye", "SearchResponse", "Update"];

        Assert.Equal(expected, Enum.GetNames<AnnouncementKind>().OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void UpnpVersionSources_AreKnown()
    {
        string[] expected = ["ControlResponse", "DeviceDescription", "Server", "ServiceDescription"];

        Assert.Equal(expected, Enum.GetNames<UpnpVersionSource>().OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
