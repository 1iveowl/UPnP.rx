using Xunit;

namespace UPnP.Rx.Tests.TestHelpers;

/// <summary>
/// The suite's shared vocabulary, deduplicated from per-file copies (dedup
/// review, 4.1.1): fixture loading, and the two fake-clock-safe async waits.
/// </summary>
internal static class TestKit
{
    /// <summary>A real-device XML fixture by file name.</summary>
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>Yields until the condition holds; asserts if it never does. No real or fake time involved.</summary>
    public static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200_000 && !condition(); i++)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "The condition was not reached.");
    }

    /// <summary>
    /// Drains pending async continuations without real or fake time - for
    /// asserting that nothing further arrives. (A fake-clock Task.Delay would
    /// never elapse here; that mistake once hung the whole run.)
    /// </summary>
    public static async Task SettleAsync()
    {
        for (var i = 0; i < 5_000; i++)
        {
            await Task.Yield();
        }
    }

    /// <summary>
    /// The device a subscription belongs to. Tests that do not exercise presence-driven
    /// cancellation still need one, so the default carries a UDN and a CONFIGID.
    /// </summary>
    public static UPnP.Rx.Eventing.DeviceIdentity Identity(string? udn = "uuid:device-1", int? configId = 1) =>
        new(udn, configId);

    /// <summary>A presence stream that never fires - the device simply stays put.</summary>
    public static Func<IObservable<UPnP.Rx.Presence.RosterChange>> NoPresence { get; } =
        () => System.Reactive.Linq.Observable.Never<UPnP.Rx.Presence.RosterChange>();

    /// <summary>
    /// A discovery envelope for presence tests. The USN is spelled as devices spell it
    /// (<c>uuid:x::upnp:rootdevice</c>), so identity matching is exercised through the
    /// same normalisation the real path uses.
    /// </summary>
    public static DiscoveredDevice DiscoveredFor(string udn, int? configId) =>
        new(SSDP.UPnP.PCL.Model.USN.Parse($"{udn}::upnp:rootdevice").Value,
            new Uri("http://192.168.1.9/desc.xml"),
            null,
            new BootSignature(1, null),
            configId,
            false,
            null,
            _ => Task.FromException<DescribedDevice>(new UpnpException("not described in these tests")));
}
