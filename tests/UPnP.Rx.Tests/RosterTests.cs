using System.Net;
using Microsoft.Extensions.Time.Testing;
using SSDP.UPnP.PCL.Model;
using UPnP.Rx.Roster;
using UPnP.Rx.Tests.TestHelpers;
using Xunit;
using static UPnP.Rx.Tests.TestHelpers.TestKit;

namespace UPnP.Rx.Tests;

public class RosterTests
{
    private const string Location = "http://192.168.1.20:1400/desc.xml";

    private const string DeviceXml =
        "<root><device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>" +
        "<friendlyName>Speaker</friendlyName><UDN>uuid:roster-1</UDN></device></root>";

    private readonly FakeControlPoint _controlPoint = new();
    private readonly FakeHttpHandler _http = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<RosterChange> _changes = [];

    private UpnpClient CreateClient() =>
        new(_controlPoint, _http.CreateClient(), new UpnpClientOptions { TimeProvider = _time }, IPAddress.Parse("192.168.1.42"));

    private void Announce(
        string usn = "uuid:roster-1::upnp:rootdevice", uint? bootId = 1, int maxAgeSeconds = 100,
        string? nls = null) =>
        _controlPoint.Notifies.OnNext(new Notify
        {
            NTS = NTS.Alive,
            Location = new Uri(Location),
            USN = USN.Parse(usn).Value,
            BOOTID = bootId,
            NLS = nls,
            CacheControl = TimeSpan.FromSeconds(maxAgeSeconds)
        });

    private void ByeBye(string usn = "uuid:roster-1::upnp:rootdevice") =>
        _controlPoint.Notifies.OnNext(new Notify
        {
            NTS = NTS.ByeBye,
            USN = USN.Parse(usn).Value,
            BOOTID = 1
        });

    [Fact]
    public async Task Announcement_Appears_RepeatDoesNot()
    {
        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);   // engine started

        Announce();
        await WaitForAsync(() => _changes.Count == 1);
        Announce();                                     // periodic re-announcement
        await SettleAsync();                            // nothing further should arrive

        var appeared = Assert.IsType<DeviceAppeared>(Assert.Single(_changes));
        Assert.False(appeared.IsReplay);
        Assert.Equal(1u, appeared.Device.BootSignature.BootId);
    }

    [Fact]
    public async Task LateSubscriber_GetsTheRoster_AsReplay()
    {
        using var client = CreateClient();
        using var s1 = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);
        Announce();
        await WaitForAsync(() => _changes.Count == 1);

        var late = new List<RosterChange>();
        using var s2 = client.Roster().Subscribe(late.Add);

        var replay = Assert.IsType<DeviceAppeared>(Assert.Single(late));
        Assert.True(replay.IsReplay);
    }

    [Fact]
    public async Task ByeByeThenAlive_SameBoot_ReportsBoth()
    {
        // The quarantine class of bug: DiscoverDevices' USN#BOOTID dedup
        // swallows a re-announcement with an unchanged BOOTID forever; the
        // roster must report the departure AND the return.
        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);

        Announce(bootId: 7);
        await WaitForAsync(() => _changes.Count == 1);
        ByeBye();
        await WaitForAsync(() => _changes.Count == 2);
        Announce(bootId: 7);                            // same boot - no reboot happened
        await WaitForAsync(() => _changes.Count == 3);

        Assert.IsType<DeviceAppeared>(_changes[0]);
        Assert.IsType<DeviceLeft>(_changes[1]);
        Assert.IsType<DeviceAppeared>(_changes[2]);
    }

    [Fact]
    public async Task Reboot_NewBootId_ReportsUpdated()
    {
        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);

        Announce(bootId: 1);
        await WaitForAsync(() => _changes.Count == 1);
        Announce(bootId: 2);
        await WaitForAsync(() => _changes.Count == 2);

        var updated = Assert.IsType<DeviceUpdated>(_changes[1]);
        Assert.Equal(2u, updated.Device.BootSignature.BootId);
    }

    [Fact]
    public async Task Reboot_Upnp10DeviceChangingNls_ReportsUpdated()
    {
        // The release's reason for existing: a device that sends no BOOTID at all
        // signals its reboot through NLS, which 4.2.0 could not see.
        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);

        Announce(bootId: null, nls: "1785066224");
        await WaitForAsync(() => _changes.Count == 1);
        Announce(bootId: null, nls: "1785099999");
        await WaitForAsync(() => _changes.Count == 2);

        var updated = Assert.IsType<DeviceUpdated>(_changes[1]);
        Assert.Null(updated.Device.BootSignature.BootId);
        Assert.Equal("1785099999", updated.Device.BootSignature.Nls);
    }

    [Fact]
    public async Task NoBootIdentity_RepeatedAnnouncements_AreNotMistakenForReboots()
    {
        // Absence must not read as change: a device announcing neither BOOTID nor
        // NLS would otherwise appear to reboot on every single announcement.
        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);

        Announce(bootId: null);
        await WaitForAsync(() => _changes.Count == 1);
        Announce(bootId: null);
        Announce(bootId: null);
        await SettleAsync();

        Assert.Single(_changes);
        Assert.IsType<DeviceAppeared>(_changes[0]);
    }

    [Fact]
    public async Task SilentVanish_ExpiresOnTheClock()
    {
        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);

        Announce(maxAgeSeconds: 100);
        await WaitForAsync(() => _changes.Count == 1);

        _time.Advance(TimeSpan.FromSeconds(101));       // sweep runs every second
        await WaitForAsync(() => _changes.Count == 2);

        Assert.IsType<DeviceExpired>(_changes[1]);
    }

    [Fact]
    public async Task Reannouncement_SlidesTheExpiryDeadline()
    {
        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);

        Announce(maxAgeSeconds: 100);
        await WaitForAsync(() => _changes.Count == 1);

        _time.Advance(TimeSpan.FromSeconds(90));
        Announce(maxAgeSeconds: 100);                   // refreshes the deadline to t=190
        await Task.Yield();
        _time.Advance(TimeSpan.FromSeconds(60));        // t=150: past the original deadline

        await SettleAsync();
        Assert.Single(_changes);                        // still just the arrival
    }

    [Fact]
    public async Task LapsedDescription_OnAlive_HealsAndReportsChange()
    {
        // Q2 self-healing: only described devices, only after the cache TTL
        // lapsed, and only when the content actually changed.
        _http.Map(Location, DeviceXml);

        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);

        Announce(maxAgeSeconds: 100);
        await WaitForAsync(() => _changes.Count == 1);
        await _changes[0].Device.GetDescriptionAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromSeconds(90));        // cache still fresh
        Announce(maxAgeSeconds: 100);
        await SettleAsync();
        Assert.Single(_changes);                        // no premature heal

        _http.Map(Location, DeviceXml.Replace("Speaker", "Speaker (renamed)"));
        _time.Advance(TimeSpan.FromSeconds(60));        // t=150: cache TTL (100) lapsed
        Announce(maxAgeSeconds: 100);
        await WaitForAsync(() => _changes.Count == 2);

        Assert.IsType<DeviceUpdated>(_changes[1]);
    }

    [Fact]
    public async Task LapsedDescription_Unchanged_StaysQuiet()
    {
        _http.Map(Location, DeviceXml);

        using var client = CreateClient();
        using var subscription = client.Roster().Subscribe(_changes.Add);
        await WaitForAsync(() => _controlPoint.SentSearches.Count > 0);

        Announce(maxAgeSeconds: 100);
        await WaitForAsync(() => _changes.Count == 1);
        await _changes[0].Device.GetDescriptionAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromSeconds(90));        // slide the roster deadline to t=190
        Announce(maxAgeSeconds: 100);
        await SettleAsync();
        _time.Advance(TimeSpan.FromSeconds(60));        // t=150: cache TTL (100) lapsed, roster alive
        Announce(maxAgeSeconds: 100);
        await SettleAsync();

        // The heal ran (cache lapsed) but the re-read was byte-identical:
        // presence only, no DeviceUpdated, no expiry.
        Assert.Single(_changes);
        Assert.IsType<DeviceAppeared>(_changes[0]);
    }

    [Fact]
    public async Task Announcements_TimelineIsUndeduplicated_KindTagged_AndClockStamped()
    {
        using var client = CreateClient();
        var seen = new List<Announcement>();
        using var subscription = client.Announcements().Subscribe(seen.Add);

        Announce();                                     // alive
        _time.Advance(TimeSpan.FromSeconds(30));
        Announce();                                     // periodic repeat - NOT deduplicated
        _controlPoint.Responses.OnNext(new MSearchResponse
        {
            Location = new Uri(Location),
            USN = USN.Parse("uuid:roster-1::upnp:rootdevice").Value,
            BOOTID = 1,
            CacheControl = TimeSpan.FromSeconds(100)
        });
        ByeBye();
        await WaitForAsync(() => seen.Count == 4);

        Assert.Equal(AnnouncementKind.Alive, seen[0].Kind);
        Assert.Equal(AnnouncementKind.Alive, seen[1].Kind);
        Assert.Equal(AnnouncementKind.SearchResponse, seen[2].Kind);
        Assert.Equal(AnnouncementKind.ByeBye, seen[3].Kind);
        Assert.Equal(TimeSpan.FromSeconds(100), seen[0].MaxAge);
        Assert.Equal(TimeSpan.Zero, seen[3].MaxAge);
        Assert.Equal(TimeSpan.FromSeconds(30), seen[1].Seen - seen[0].Seen);
        Assert.Empty(_controlPoint.SentSearches);       // passive: no M-SEARCH sent
    }

    [Fact]
    public async Task SearchAsync_Solicits_WithoutAnySubscription()
    {
        using var client = CreateClient();

        await client.SearchAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(_controlPoint.SentSearches);
    }

    [Fact]
    public void SubscribeAfterClientDisposal_CompletesImmediately()
    {
        var client = CreateClient();
        var roster = client.Roster();
        client.Dispose();

        var completed = false;
        using var subscription = roster.Subscribe(_ => { }, _ => { }, () => completed = true);

        Assert.True(completed);
    }
}
