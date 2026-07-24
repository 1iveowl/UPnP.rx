using System.Reactive.Subjects;
using DynamicData;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Sample.Dashboard.Client.Models;

namespace Sample.Dashboard.Client.Services;

/// <summary>
/// Turns the server's SignalR stream into client-side Rx: DeviceUp/DeviceGone
/// feed a DynamicData cache keyed by device identity. No sockets in the
/// browser - the server does the listening.
/// </summary>
public sealed class DeviceStreamClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly BehaviorSubject<string> _state = new("connecting…");

    public DeviceStreamClient(NavigationManager navigation)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri("/devicehub"))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<DeviceDto>("DeviceUp", dto => Cache.AddOrUpdate(dto));
        _connection.On<string>("DeviceGone", key => Cache.RemoveKey(key));

        _connection.Reconnecting += _ =>
        {
            _state.OnNext("reconnecting…");
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            Cache.Clear();                      // the hub replays the roster on reconnect
            _state.OnNext("live");
            return Task.CompletedTask;
        };
        _connection.Closed += _ =>
        {
            _state.OnNext("disconnected");
            return Task.CompletedTask;
        };
    }

    /// <summary>The live device roster, keyed by device identity.</summary>
    public SourceCache<DeviceDto, string> Cache { get; } = new(d => d.Key);

    /// <summary>Connection state as a stream: connecting… / live / reconnecting… / disconnected.</summary>
    public IObservable<string> State => _state;

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
        Cache.Dispose();
    }
}
