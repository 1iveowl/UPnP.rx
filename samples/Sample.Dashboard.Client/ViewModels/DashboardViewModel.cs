using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using Sample.Dashboard.Client.Models;
using Sample.Dashboard.Client.Services;

namespace Sample.Dashboard.Client.ViewModels;

/// <summary>
/// Rx end to end: UPnP.Rx observables on the server, SignalR in the middle,
/// DynamicData + ReactiveUI here. Every cache changeset bumps
/// <see cref="Revision"/>, whose property change notification is what makes the
/// component re-render - adds, removals and in-place updates alike.
/// </summary>
public sealed class DashboardViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _cleanup = [];
    private readonly ReadOnlyObservableCollection<DeviceDto> _devices;
    private readonly ObservableAsPropertyHelper<int> _count;
    private readonly ObservableAsPropertyHelper<int> _revision;
    private readonly ObservableAsPropertyHelper<string> _status;
    private string _filter = string.Empty;

    public DashboardViewModel(DeviceStreamClient client)
    {
        // The visible list: filtered by the search box, sorted by name.
        var predicate = this.WhenAnyValue(vm => vm.Filter)
            .Select<string, Func<DeviceDto, bool>>(filter => device =>
                string.IsNullOrWhiteSpace(filter)
                || (device.FriendlyName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (device.Manufacturer?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (device.Root.DeviceType?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));

        _cleanup.Add(client.Cache
            .Connect()
            .Filter(predicate)
            .SortAndBind(
                out _devices,
                SortExpressionComparer<DeviceDto>.Ascending(d => d.FriendlyName ?? "~"))
            .Subscribe());

        // The render pump: one tick per changeset, so the page re-renders on
        // every roster change - including in-place device updates.
        _revision = client.Cache.Connect()
            .Select((_, index) => index)
            .ToProperty(this, vm => vm.Revision);
        _cleanup.Add(_revision);

        _count = client.Cache.CountChanged
            .ToProperty(this, vm => vm.Count);
        _cleanup.Add(_count);

        _status = client.State
            .ToProperty(this, vm => vm.Status, initialValue: "connecting…");
        _cleanup.Add(_status);
    }

    /// <summary>The roster: filtered, sorted by friendly name.</summary>
    public ReadOnlyObservableCollection<DeviceDto> Devices => _devices;

    /// <summary>Devices currently on the network (unfiltered).</summary>
    public int Count => _count.Value;

    /// <summary>Bumps on every cache changeset; its notification drives re-rendering.</summary>
    public int Revision => _revision.Value;

    /// <summary>Hub connection state.</summary>
    public string Status => _status.Value;

    /// <summary>Search text; filters by name, manufacturer or device type.</summary>
    public string Filter
    {
        get => _filter;
        set => this.RaiseAndSetIfChanged(ref _filter, value);
    }

    public void Dispose() => _cleanup.Dispose();
}
