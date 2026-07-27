using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Sample.Dashboard.Client.Models;

namespace Sample.Dashboard.Client.Services;

/// <summary>
/// Turns the server's SignalR stream into client-side Rx: DeviceUp/DeviceGone
/// feed a DynamicData cache keyed by device identity. No sockets in the
/// browser - the server does the listening. Mutation stays private; consumers
/// get read-only views.
/// Serialization assumption (review RX-7): subjects and caches here are only
/// mutated from SignalR callbacks - dispatched sequentially per connection -
/// on Blazor WASM's single thread. Porting this class to Blazor Server means
/// multiple dispatch contexts: wrap the mutations (Observable.Synchronize /
/// Subject.Synchronize) before reusing it there.
/// </summary>
public sealed class DeviceStreamClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly SourceCache<DeviceDto, string> _cache = new(d => d.Key);
    private readonly BehaviorSubject<string> _state = new("connecting…");
    private readonly BehaviorSubject<bool> _rescanning = new(false);
    private readonly Subject<LeaseEventDto> _leaseEvents = new();
    private readonly Subject<SsdpActivityDto> _ssdpActivity = new();
    private readonly Dictionary<string, List<SsdpActivityDto>> _activity = [];
    private readonly Dictionary<string, int> _activityCount = [];

    // The sample's retention config. Rows cap hard per device; the age bound
    // (relative to the newest row, so no client clock involved) keeps a
    // long-running page honest - an hour-old rhythm misleads more than it
    // informs. Long retention belongs in a file log, deliberately not a
    // feature of this sample.
    private const int _maxActivityRows = 20;
    private static readonly TimeSpan _activityMaxAge = TimeSpan.FromHours(1);

    private readonly System.Reactive.Subjects.Subject<string> _reboots = new();
    private IDisposable? _rescanFallback;

    public DeviceStreamClient(NavigationManager navigation)
    {
        Devices = _cache.AsObservableCache();

        _connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri(HubEvents.Path))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<DeviceDto>(HubEvents.DeviceUp, dto =>
        {
            // The first fresh device after a rescan is the reset moment: the
            // grayed stale list goes, the new one starts building.
            if (_rescanning.Value)
            {
                _rescanFallback?.Dispose();
                _cache.Clear();
                _rescanning.OnNext(false);
            }

            _cache.AddOrUpdate(dto);
        });
        _connection.On<string>(HubEvents.DeviceGone, key =>
        {
            _cache.RemoveKey(key);
            // A departed device's log leaves with it - rings for devices that
            // never return must not accumulate on a long-running page.
            _activity.Remove(key);
            _activityCount.Remove(key);
        });
        _connection.On<string>(HubEvents.DeviceRebooted, key => _reboots.OnNext(key));
        _connection.On<LeaseEventDto>(HubEvents.LeaseEvent, e => _leaseEvents.OnNext(e));
        _connection.On<SsdpActivityDto>(HubEvents.SsdpActivity, dto =>
        {
            // Newest-first, capped per device; live-only (a fresh page starts
            // empty until traffic arrives - retention is deliberately client-side).
            if (!_activity.TryGetValue(dto.DeviceKey, out var rows))
            {
                _activity[dto.DeviceKey] = rows = [];
            }

            rows.Insert(0, dto);

            while (rows.Count > _maxActivityRows
                || (rows.Count > 1 && dto.Seen - rows[^1].Seen > _activityMaxAge))
            {
                rows.RemoveAt(rows.Count - 1);
            }

            _activityCount[dto.DeviceKey] = _activityCount.GetValueOrDefault(dto.DeviceKey) + 1;
            _ssdpActivity.OnNext(dto);
        });
        // A rescan (from any client) resets the roster. The stale cards stay
        // up - grayed via Rescanning - until the first fresh device arrives;
        // watch subscriptions end right away (DeviceNode observes Rescanning).
        _connection.On(HubEvents.RosterReset, () =>
        {
            _rescanning.OnNext(true);
            _rescanFallback?.Dispose();
            _rescanFallback = Observable
                .Timer(TimeSpan.FromSeconds(10), DefaultScheduler.Instance)
                .Subscribe(_ =>
                {
                    // Nothing answered the new search: stale cards would only
                    // mislead - clear anyway and fall back to the empty state.
                    if (_rescanning.Value)
                    {
                        _cache.Clear();
                        _rescanning.OnNext(false);
                    }
                });
        });

        _connection.Reconnecting += _ =>
        {
            _state.OnNext("reconnecting…");
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            _cache.Clear();                     // the hub replays the roster on reconnect
            _state.OnNext("live");
            return Task.CompletedTask;
        };
        _connection.Closed += _ =>
        {
            _state.OnNext("disconnected");
            return Task.CompletedTask;
        };
    }

    /// <summary>The live device roster, keyed by device identity. Read-only view.</summary>
    public IObservableCache<DeviceDto, string> Devices { get; }

    /// <summary>Connection state as a stream: connecting… / live / reconnecting… / disconnected.</summary>
    public IObservable<string> State => _state.AsObservable();

    /// <summary>
    /// True from a rescan's RosterReset until the first fresh device arrives
    /// (or a 10 s fallback fires on a silent network). The page grays the stale
    /// cards; device nodes end their live watches on the rising edge.
    /// </summary>
    public IObservable<bool> Rescanning => _rescanning.AsObservable();

    /// <summary>Renewal-lifecycle events from server-held leases, live from the hub.</summary>
    public IObservable<LeaseEventDto> LeaseEvents => _leaseEvents.AsObservable();

    /// <summary>Each SSDP activity row as it arrives - subscribe to re-render open timelines.</summary>
    public IObservable<SsdpActivityDto> SsdpActivity => _ssdpActivity.AsObservable();

    /// <summary>
    /// Device keys as they restart. A reboot voids everything held about a device
    /// (UDA 2.0 clause 1.2.4), including any live event subscription, so it is worth
    /// showing rather than folding into an ordinary refresh.
    /// </summary>
    public IObservable<string> Reboots => _reboots;

    /// <summary>The capped, newest-first activity log for one device.</summary>
    public IReadOnlyList<SsdpActivityDto> ActivityFor(string deviceKey) =>
        _activity.TryGetValue(deviceKey, out var rows) ? rows : [];

    /// <summary>Every message seen for the device since page load (the ring caps rows, not this).</summary>
    public int ActivityCountFor(string deviceKey) => _activityCount.GetValueOrDefault(deviceKey);

    /// <summary>The gateway's identity + WAN state, or null when none was found (or not connected).</summary>
    public Task<GatewayDto?> GetGatewayInfoAsync() =>
        InvokeAsync<GatewayDto?>(HubEvents.GetGatewayInfo, fallback: null);

    /// <summary>The gateway's current mapping table.</summary>
    public Task<PortMappingDto[]> GetPortMappingsAsync() =>
        InvokeAsync<PortMappingDto[]>(HubEvents.GetPortMappings, fallback: []);

    /// <summary>Creates an auto-renewing mapping; returns an error message or null.</summary>
    public Task<string?> AddPortMappingAsync(ushort externalPort, ushort internalPort, string protocol, string description, int leaseMinutes) =>
        InvokeAsync<string?>(HubEvents.AddPortMapping, "Not connected to the server.",
            externalPort, internalPort, protocol, description, leaseMinutes);

    /// <summary>Deletes a mapping; returns an error message or null.</summary>
    public Task<string?> DeletePortMappingAsync(ushort externalPort, string protocol) =>
        InvokeAsync<string?>(HubEvents.DeletePortMapping, "Not connected to the server.", externalPort, protocol);

    /// <summary>Invokes a SOAP action on a service; validation, faults and results all arrive as <see cref="InvokeResultDto"/>.</summary>
    public Task<InvokeResultDto> InvokeActionAsync(
        string deviceKey, string? udn, string serviceType, string actionName, Dictionary<string, string> arguments) =>
        InvokeAsync<InvokeResultDto>(HubEvents.InvokeAction,
            new InvokeResultDto([], "Not connected to the server."),
            deviceKey, udn!, serviceType, actionName, arguments);

    /// <summary>Invalidates + re-reads one device's description; the result arrives as a DeviceUp broadcast.</summary>
    public Task<string?> RefreshDeviceAsync(string key) =>
        InvokeAsync<string?>(HubEvents.RefreshDevice, "Not connected to the server.", key);

    /// <summary>Sends one M-SEARCH burst - populates activity logs and refreshes presence without resetting anything.</summary>
    public async Task ProbeAsync()
    {
        if (_connection.State is not HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _connection.InvokeAsync(HubEvents.Probe);
        }
        catch (Exception)
        {
            // Silence is acceptable: the log staying empty is the signal.
        }
    }

    /// <summary>
    /// Clears the server roster and searches the network afresh. Every client
    /// enters rescan mode via the RosterReset broadcast: live watches end at
    /// once, stale cards stay grayed, and the list resets when the first fresh
    /// device answers.
    /// </summary>
    public async Task RescanAsync()
    {
        if (_connection.State is not HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _connection.InvokeAsync(HubEvents.Rescan);
        }
        catch (Exception)
        {
            // The roster staying put is the visible signal; nothing to surface here.
        }
    }

    /// <summary>
    /// A service's live GENA events as an observable: subscribing opens a hub
    /// stream (which subscribes on the device when it is the first watcher);
    /// disposing cancels it (UNSUBSCRIBE when it was the last).
    /// </summary>
    public IObservable<ServiceEventDto> ServiceEvents(string deviceKey, string? udn, string serviceType) =>
        System.Reactive.Linq.Observable.Create<ServiceEventDto>(async (observer, ct) =>
        {
            if (_connection.State is not HubConnectionState.Connected)
            {
                observer.OnError(new InvalidOperationException("Not connected to the server."));
                return;
            }

            try
            {
                await foreach (var e in _connection
                    .StreamAsync<ServiceEventDto>(HubEvents.StreamServiceEvents, deviceKey, udn, serviceType, ct)
                    .WithCancellation(ct))
                {
                    observer.OnNext(e);
                }

                observer.OnCompleted();
            }
            catch (OperationCanceledException)
            {
                // Unsubscribed - normal end.
            }
            catch (Exception e)
            {
                observer.OnError(e);
            }
        });

    private async Task<T> InvokeAsync<T>(string method, T fallback, params object[] arguments)
    {
        if (_connection.State is not HubConnectionState.Connected)
        {
            return fallback;
        }

        try
        {
            return await _connection.InvokeCoreAsync<T>(method, arguments);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>Fetches a service's SCPD detail from the server on demand.</summary>
    public async Task<ServiceDetailDto> GetServiceDetailAsync(string deviceKey, string? udn, string serviceType)
    {
        if (_connection.State is not HubConnectionState.Connected)
        {
            return new ServiceDetailDto(serviceType, [], [], "Not connected to the server.");
        }

        try
        {
            return await _connection.InvokeAsync<ServiceDetailDto>(
                HubEvents.GetServiceDetail, deviceKey, udn, serviceType);
        }
        catch (Exception e)
        {
            return new ServiceDetailDto(serviceType, [], [], e.Message);
        }
    }

    public async Task StartAsync()
    {
        // Components may mount more than once; only start from cold.
        if (_connection.State is not HubConnectionState.Disconnected)
        {
            return;
        }

        try
        {
            await _connection.StartAsync();
            _state.OnNext("live");
        }
        catch (Exception)
        {
            _state.OnNext("server unreachable");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _rescanFallback?.Dispose();
        _ssdpActivity.Dispose();
        _rescanning.Dispose();
        _state.Dispose();
        _leaseEvents.Dispose();
        Devices.Dispose();
        _cache.Dispose();
    }
}
