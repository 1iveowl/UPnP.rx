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
/// </summary>
public sealed class DeviceStreamClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly SourceCache<DeviceDto, string> _cache = new(d => d.Key);
    private readonly BehaviorSubject<string> _state = new("connecting…");
    private readonly Subject<LeaseEventDto> _leaseEvents = new();

    public DeviceStreamClient(NavigationManager navigation)
    {
        Devices = _cache.AsObservableCache();

        _connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri(HubEvents.Path))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<DeviceDto>(HubEvents.DeviceUp, dto => _cache.AddOrUpdate(dto));
        _connection.On<string>(HubEvents.DeviceGone, key => _cache.RemoveKey(key));
        _connection.On<LeaseEventDto>(HubEvents.LeaseEvent, e => _leaseEvents.OnNext(e));

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

    /// <summary>Renewal-lifecycle events from server-held leases, live from the hub.</summary>
    public IObservable<LeaseEventDto> LeaseEvents => _leaseEvents.AsObservable();

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

    /// <summary>Invalidates + re-reads one device's description; the result arrives as a DeviceUp broadcast.</summary>
    public Task<string?> RefreshDeviceAsync(string key) =>
        InvokeAsync<string?>(HubEvents.RefreshDevice, "Not connected to the server.", key);

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
        _state.Dispose();
        _leaseEvents.Dispose();
        Devices.Dispose();
        _cache.Dispose();
    }
}
