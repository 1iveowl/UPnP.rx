using Microsoft.Extensions.Time.Testing;
using UPnP.Rx.Eventing;
using UPnP.Rx.Parsing;
using Xunit;

namespace UPnP.Rx.Tests;

/// <summary>
/// The cache's policies, tested directly (the extraction's payoff): TTL,
/// generation eviction, failure eviction, invalidation, self-heal state.
/// </summary>
public sealed class DescriptionCacheTests : IDisposable
{
    private static readonly Uri _location = new("http://192.168.1.40:1400/desc.xml");

    private readonly FakeTimeProvider _time = new();
    private readonly DescriptionCache _cache;
    private readonly HttpClient _http = new();
    private readonly EventingContext _eventing;
    private int _fetches;

    public DescriptionCacheTests()
    {
        _cache = new DescriptionCache(_time);
        _eventing = new EventingContext(_http, new UpnpClientOptions(), CancellationToken.None);
    }

    private Task<DescribedDevice> Fetch(string hash = "H1")
    {
        _fetches++;
        return Task.FromResult(new DescribedDevice(
            DescriptionParser.ParseDeviceDescription(
                "<root><device><UDN>uuid:cache-test</UDN></device></root>", _location).Value!,
            _http, new UpnpClientOptions(), _eventing, null, CancellationToken.None, hash));
    }

    private Task<DescribedDevice> GetAsync(uint bootId = 1, int maxAgeSeconds = 100, string hash = "H1") =>
        _cache.GetOrFetchAsync(
            _location, configId: null, bootId, TimeSpan.FromSeconds(maxAgeSeconds),
            () => Fetch(hash), TestContext.Current.CancellationToken);

    [Fact]
    public async Task SecondRead_WithinTtl_IsCached()
    {
        await GetAsync();
        await GetAsync();

        Assert.Equal(1, _fetches);
        Assert.Equal(1, _cache.Count);
    }

    [Fact]
    public async Task TtlLapse_Refetches_WithoutAccumulating()
    {
        await GetAsync(maxAgeSeconds: 100);
        _time.Advance(TimeSpan.FromSeconds(101));
        await GetAsync(maxAgeSeconds: 100);

        Assert.Equal(2, _fetches);
        Assert.Equal(1, _cache.Count);
    }

    [Fact]
    public async Task NewBootGeneration_EvictsTheOld()
    {
        await GetAsync(bootId: 1);
        await GetAsync(bootId: 2);

        Assert.Equal(2, _fetches);
        Assert.Equal(1, _cache.Count);                   // the flappy-device guarantee
    }

    [Fact]
    public async Task FailedFetch_IsNotCached_NextCallRetries()
    {
        var attempts = 0;
        Task<DescribedDevice> FailingFetch()
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<DescribedDevice>(new UpnpException("unreachable"))
                : Fetch();
        }

        await Assert.ThrowsAsync<UpnpException>(() => _cache.GetOrFetchAsync(
            _location, null, 1, TimeSpan.FromSeconds(100), FailingFetch, TestContext.Current.CancellationToken));

        var described = await _cache.GetOrFetchAsync(
            _location, null, 1, TimeSpan.FromSeconds(100), FailingFetch, TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        Assert.Equal("uuid:cache-test", described.Description.Udn);
    }

    [Fact]
    public async Task State_ReportsHashAndExpiry_ForTheSelfHeal()
    {
        Assert.Equal((false, null), _cache.State(_location));           // never described

        await GetAsync(maxAgeSeconds: 100, hash: "H1");
        Assert.Equal((false, "H1"), _cache.State(_location));

        _time.Advance(TimeSpan.FromSeconds(101));
        Assert.Equal((true, "H1"), _cache.State(_location));            // lapsed: heal territory
    }

    [Fact]
    public async Task Invalidate_DropsEveryGeneration()
    {
        await GetAsync(bootId: 1);
        await GetAsync(bootId: 2);

        _cache.Invalidate(_location);

        Assert.Equal(0, _cache.Count);
    }

    public void Dispose()
    {
        _eventing.Dispose();
        _http.Dispose();
    }
}
