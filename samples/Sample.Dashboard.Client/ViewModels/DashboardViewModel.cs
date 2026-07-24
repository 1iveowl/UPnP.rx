using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using Sample.Dashboard.Client.Models;
using Sample.Dashboard.Client.Services;

namespace Sample.Dashboard.Client.ViewModels;

/// <summary>
/// Rx end to end: UPnP.Rx observables on the server, SignalR in the middle,
/// DynamicData cache + ReactiveUI bindings here. The view re-renders off
/// ordinary property change notifications.
/// </summary>
public sealed class DashboardViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _cleanup = [];
    private readonly ReadOnlyObservableCollection<DeviceDto> _devices;
    private readonly ObservableAsPropertyHelper<int> _count;
    private readonly ObservableAsPropertyHelper<string> _status;

    public DashboardViewModel(DeviceStreamClient client)
    {
        _cleanup.Add(client.Cache
            .Connect()
            .SortAndBind(
                out _devices,
                SortExpressionComparer<DeviceDto>.Ascending(d => d.FriendlyName ?? "~"))
            .Subscribe());

        _count = client.Cache.CountChanged
            .ToProperty(this, vm => vm.Count);
        _cleanup.Add(_count);

        _status = client.State
            .ToProperty(this, vm => vm.Status, initialValue: "connecting…");
        _cleanup.Add(_status);
    }

    /// <summary>The roster, sorted by friendly name.</summary>
    public ReadOnlyObservableCollection<DeviceDto> Devices => _devices;

    /// <summary>Devices currently on the network.</summary>
    public int Count => _count.Value;

    /// <summary>Hub connection state.</summary>
    public string Status => _status.Value;

    public void Dispose() => _cleanup.Dispose();
}
