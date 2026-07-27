using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SSDP.UPnP.PCL.Model;
using UPnP.Rx.Eventing;
using UPnP.Rx.Presence;
using UPnP.Rx.Tests.TestHelpers;
using Xunit;
using static UPnP.Rx.Tests.TestHelpers.TestKit;

namespace UPnP.Rx.Tests;

/// <summary>
/// Boundedness under load: every stateful structure the library owns must be
/// sized by what is PRESENT (devices, variables, subscribers), never by what
/// has PASSED THROUGH (announcements, notifies, subscribe cycles). Structural
/// assertions carry the leak proof; one generous heap-delta assertion guards
/// against gross regressions (generous because parallel tests share the heap).
/// </summary>
public class SoakTests
{
    private const string Location = "http://192.168.1.30:1400/desc.xml";

    [Fact]
    public async Task Roster_StateIsBoundedByPresence_NotByTraffic()
    {
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        var time = new FakeTimeProvider();
        using var client = new UpnpClient(
            controlPoint, http.CreateClient(), new UpnpClientOptions { TimeProvider = time },
            IPAddress.Parse("192.168.1.42"));

        var appeared = 0;
        using var subscription = client.Roster().Subscribe(change =>
        {
            if (change is DeviceAppeared { IsReplay: false })
            {
                appeared++;
            }
        });
        await WaitForAsync(() => controlPoint.SentSearches.Count > 0);

        const int devices = 200;
        const int rounds = 25;                           // 5000 announcements total

        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var round = 0; round < rounds; round++)
        {
            for (var device = 0; device < devices; device++)
            {
                controlPoint.Notifies.OnNext(new Notify
                {
                    NTS = NTS.Alive,
                    Location = new Uri($"http://192.168.2.{device % 250}:1400/d{device}.xml"),
                    USN = USN.Parse($"uuid:soak-{device}::upnp:rootdevice").Value,
                    BOOTID = 1,
                    MaxAge = TimeSpan.FromSeconds(300)
                });
            }
        }

        await WaitForAsync(() => appeared == devices);

        // The roster's whole state is what a late subscriber receives: it must
        // reflect presence (200 devices), not traffic (5000 announcements).
        var replay = new List<RosterChange>();
        using var late = client.Roster().Subscribe(replay.Add);
        Assert.Equal(devices, replay.Count);

        var after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.True(after - before < 10_000_000,
            $"Heap grew by {(after - before) / 1024} KB across the announcement flood.");
    }

    [Fact]
    public async Task Eventing_LastKnownIsBoundedByVariables_NotByNotifies()
    {
        var time = new FakeTimeProvider();
        Func<NotifyRequest, CancellationToken, Task>? route = null;
        var source = new GenaSubscriptionSource(
            new Uri("http://192.168.1.9/event/sub"),
            token => new Uri($"http://192.168.1.42:49500/upnp/events/{token}"),
            new SoakTransport(),
            (_, handler) =>
            {
                route = handler;
                return System.Reactive.Disposables.Disposable.Create(() => route = null);
            },
            new UpnpClientOptions { TimeProvider = time },
            NullLogger.Instance,
            CancellationToken.None);

        var received = 0;
        using var subscription = source.Subscribe(_ => received++);
        await WaitForAsync(() => route is not null);

        const int notifies = 5_000;
        const int variables = 8;

        for (var i = 0; i < notifies; i++)
        {
            var name = $"Var{i % variables}";
            await route!(new NotifyRequest("uuid:sid-1", null,
                $"<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><{name}>{i}</{name}></e:property></e:propertyset>"),
                CancellationToken.None);
        }

        await WaitForAsync(() => received >= notifies);

        // Replay size == variable count: last-known state, not history.
        var replay = new List<UpnpEvent>();
        using var late = source.Subscribe(replay.Add);
        Assert.Equal(variables, replay.Count(e => e is PropertyChange { IsReplay: true }));
    }

    [Fact]
    public async Task SubscribeDisposeChurn_LeavesNoObserversOrEngines()
    {
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        var time = new FakeTimeProvider();
        using var client = new UpnpClient(
            controlPoint, http.CreateClient(), new UpnpClientOptions { TimeProvider = time },
            IPAddress.Parse("192.168.1.42"));

        for (var i = 0; i < 500; i++)
        {
            client.Roster().Subscribe(_ => { }).Dispose();
        }

        // Still fully functional after the churn, and replay proves clean state.
        var replay = new List<RosterChange>();
        using var subscription = client.Roster().Subscribe(replay.Add);
        await WaitForAsync(() => controlPoint.SentSearches.Count >= 501);
        Assert.Empty(replay);
    }

    [Fact]
    public async Task DescriptionCache_KeepsOnlyTheNewestGeneration_AcrossReboots()
    {
        var controlPoint = new FakeControlPoint();
        var http = new FakeHttpHandler();
        var time = new FakeTimeProvider();
        using var client = new UpnpClient(
            controlPoint, http.CreateClient(), new UpnpClientOptions { TimeProvider = time });
        http.Map(Location, "<root><device><deviceType>urn:d</deviceType><UDN>uuid:soak-cache</UDN></device></root>");

        var seen = new List<DiscoveredDevice>();
        using var subscription = client.DiscoverDevices().Subscribe(seen.Add);

        for (uint boot = 1; boot <= 30; boot++)
        {
            controlPoint.Notifies.OnNext(new Notify
            {
                NTS = NTS.Alive,
                Location = new Uri(Location),
                USN = USN.Parse("uuid:soak-cache::upnp:rootdevice").Value,
                BOOTID = boot
            });
            await seen[^1].GetDescriptionAsync(TestContext.Current.CancellationToken);
        }

        // 30 boots described; a flappy device must not accumulate generations.
        Assert.Equal(1, client.DescriptionCacheCount);
    }

    private sealed class SoakTransport : IGenaTransport
    {
        public Task<(string Sid, TimeSpan? Timeout)> SubscribeAsync(
            Uri eventSubUrl, Uri callback, TimeSpan requestedTimeout, CancellationToken ct) =>
            Task.FromResult(("uuid:sid-1", (TimeSpan?)TimeSpan.FromMinutes(30)));

        public Task RenewAsync(Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
