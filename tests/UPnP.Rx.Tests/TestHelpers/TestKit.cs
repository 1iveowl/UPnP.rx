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

    /// <summary>
    /// Yields until the condition holds; asserts if it never does. No real or fake time
    /// involved, so it is the right wait for work that completes on continuations of
    /// the current flow. It is the WRONG wait for work handed to another thread - the
    /// yields can all run while the other thread is still queued, which is exactly how
    /// a two-core CI runner fails a test that passes on a big machine. Use
    /// <see cref="WaitForRealTimeAsync"/> there.
    /// </summary>
    public static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200_000 && !condition(); i++)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "The condition was not reached.");
    }

    /// <summary>
    /// Polls on the system clock until the condition holds. For work that genuinely
    /// crosses threads - anything downstream of a <c>SubscribeOn</c> or a thread-pool
    /// hand-off - where counting yields proves nothing about the other thread's
    /// progress. Deliberately the system clock: a fake one would never advance here.
    /// </summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">How long to allow; generous by default, since it only elapses on failure.</param>
    public static async Task WaitForRealTimeAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = TimeProvider.System.GetUtcNow() + (timeout ?? TimeSpan.FromSeconds(10));

        while (!condition() && TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        Assert.True(condition(), "The condition was not reached within the timeout.");
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
