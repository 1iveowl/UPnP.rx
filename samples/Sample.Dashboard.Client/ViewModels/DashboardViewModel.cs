using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Sample.Dashboard.Client.Models;
using Sample.Dashboard.Client.Services;

namespace Sample.Dashboard.Client.ViewModels;

/// <summary>
/// Rx end to end: UPnP.Rx observables on the server, SignalR in the middle,
/// DynamicData + ReactiveUI here. One pipeline drives everything visible:
/// filter and sort produce the bound collection, and the same changesets bump
/// <see cref="Revision"/>, whose property change notification re-renders the
/// component - adds, removals, in-place updates and filter changes alike.
/// </summary>
public sealed partial class DashboardViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _cleanup = [];
    private readonly ReadOnlyObservableCollection<DeviceDto> _devices;
    private readonly ObservableAsPropertyHelper<int> _count;
    private readonly ObservableAsPropertyHelper<int> _revision;
    private readonly ObservableAsPropertyHelper<string> _status;
    private readonly ObservableAsPropertyHelper<bool> _showHints;

    /// <summary>Search text; filters by name, manufacturer or device type (property source-generated).</summary>
    [Reactive]
    private string _filter = string.Empty;

    public DashboardViewModel(DeviceStreamClient client)
    {
        var predicate = this.WhenAnyValue(vm => vm.Filter)
            .Select<string, Func<DeviceDto, bool>>(filter => device =>
                string.IsNullOrWhiteSpace(filter)
                || (device.FriendlyName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (device.Manufacturer?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (device.Root.DeviceType?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));

        // One subscription: filter, sort, bind - and count changesets as the
        // render pump on the way out.
        _revision = client.Devices
            .Connect()
            .Filter(predicate)
            .SortAndBind(
                out _devices,
                SortExpressionComparer<DeviceDto>.Ascending(d => d.FriendlyName ?? "~"))
            .Select((_, index) => index)
            .ToProperty(this, vm => vm.Revision);
        _cleanup.Add(_revision);

        _count = client.Devices.CountChanged
            .ToProperty(this, vm => vm.Count);
        _cleanup.Add(_count);

        _status = client.State
            .ToProperty(this, vm => vm.Status, initialValue: "connecting…");
        _cleanup.Add(_status);

        // Declarative empty-state hints: visible once 5 s have passed AND the
        // roster is still empty - and gone again the moment a device shows up.
        // Explicit scheduler per the house time-model heuristic (a TimeSpan
        // operator never rides the implicit default); the sample deliberately
        // runs the real clock - it has no FakeTimeProvider tests. (WASM is
        // single-threaded, so DefaultScheduler's timer callbacks land on the
        // one thread anyway; ReactiveUI's own scheduler type trips a WasmRuntime
        // type initializer here.)
        _showHints = Observable
            .Timer(TimeSpan.FromSeconds(5), DefaultScheduler.Instance)
            .Select(_ => true)
            .StartWith(false)
            .CombineLatest(client.Devices.CountChanged, (elapsed, count) => elapsed && count == 0)
            .DistinctUntilChanged()
            .ToProperty(this, vm => vm.ShowHints);
        _cleanup.Add(_showHints);
    }

    /// <summary>The roster: filtered, sorted by friendly name.</summary>
    public ReadOnlyObservableCollection<DeviceDto> Devices => _devices;

    /// <summary>Devices currently on the network (unfiltered).</summary>
    public int Count => _count.Value;

    /// <summary>Bumps on every visible changeset; its notification drives re-rendering.</summary>
    public int Revision => _revision.Value;

    /// <summary>Hub connection state.</summary>
    public string Status => _status.Value;

    /// <summary>True when the network has stayed empty for ~5 s - shows the troubleshooting hints.</summary>
    public bool ShowHints => _showHints.Value;

    public void Dispose() => _cleanup.Dispose();
}
