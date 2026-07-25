using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Sample.Dashboard.Client.Models;
using Sample.Dashboard.Client.Services;

namespace Sample.Dashboard.Client.ViewModels;

/// <summary>
/// The port-mapping page's view model: ReactiveCommands for load/add/delete,
/// OAPHs for everything the view renders, and a DynamicData-bound live feed of
/// lease renewal events streaming from the server-held leases.
/// </summary>
public sealed partial class PortMappingViewModel : ReactiveObject, IDisposable
{
    private const int MaxEventRows = 25;

    private readonly CompositeDisposable _cleanup = [];
    private readonly SourceList<LeaseEventDto> _events = new();
    private readonly ReadOnlyObservableCollection<LeaseEventDto> _eventRows;
    private readonly ObservableAsPropertyHelper<GatewayDto?> _gateway;
    private readonly ObservableAsPropertyHelper<PortMappingDto[]> _mappings;
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    private readonly ObservableAsPropertyHelper<bool> _canAdd;
    private readonly ObservableAsPropertyHelper<int> _eventsRevision;

    // Settable form state: properties are source-generated ([Reactive], review
    // RUI-7). OAPHs and commands stay hand-written on purpose - the command
    // outputScheduler must be explicit on WASM (Rx 7 scheduler defect), and
    // the OAPH pipelines document themselves better in full.
    /// <summary>The last add/delete error from the server, or null.</summary>
    [Reactive(SetModifier = AccessModifier.Private)]
    private string? _lastError;

    /// <summary>WAN-side port for the new mapping.</summary>
    [Reactive]
    private ushort _externalPort = 18080;

    /// <summary>LAN-side port for the new mapping.</summary>
    [Reactive]
    private ushort _internalPort = 18080;

    /// <summary>TCP or UDP.</summary>
    [Reactive]
    private string _protocol = "TCP";

    /// <summary>Description stored on the gateway.</summary>
    [Reactive]
    private string _description = "";

    /// <summary>Lease length in minutes (auto-renewed at half-life by the server).</summary>
    [Reactive]
    private int _leaseMinutes = 60;

    // System.Reactive 7's WASM scheduler enlightenment rejects the .NET 10
    // runtime ("does not support this version of the WebAssembly scheduler"),
    // and ReactiveCommand's DEFAULT output scheduler resolves to it - so every
    // command gets an explicit scheduler. WASM is single-threaded; the current
    // thread IS the UI thread.
    private static readonly IScheduler _wasmSafeScheduler = CurrentThreadScheduler.Instance;

    public PortMappingViewModel(DeviceStreamClient client)
    {
        LoadCommand = ReactiveCommand.CreateFromTask(async () =>
            (Gateway: await client.GetGatewayInfoAsync(), Mappings: await client.GetPortMappingsAsync()),
            outputScheduler: _wasmSafeScheduler);

        _gateway = LoadCommand.Select(r => r.Gateway).ToProperty(this, vm => vm.Gateway);
        _cleanup.Add(_gateway);
        _mappings = LoadCommand.Select(r => r.Mappings).ToProperty(this, vm => vm.Mappings, initialValue: []);
        _cleanup.Add(_mappings);

        var canAdd = this.WhenAnyValue(
            vm => vm.ExternalPort, vm => vm.InternalPort, vm => vm.LeaseMinutes,
            (external, internalPort, minutes) => external > 0 && internalPort > 0 && minutes >= 1);

        AddCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            LastError = await client.AddPortMappingAsync(
                ExternalPort, InternalPort, Protocol, Description, LeaseMinutes);
        }, canAdd, _wasmSafeScheduler);

        DeleteCommand = ReactiveCommand.CreateFromTask<PortMappingDto>(async mapping =>
        {
            LastError = await client.DeletePortMappingAsync(mapping.ExternalPort, mapping.Protocol);
        }, outputScheduler: _wasmSafeScheduler);

        _canAdd = canAdd.ToProperty(this, vm => vm.CanAdd, initialValue: true);
        _cleanup.Add(_canAdd);

        // Add/delete completion refreshes the table.
        _cleanup.Add(AddCommand.Merge(DeleteCommand)
            .Select(_ => Unit.Default)
            .InvokeCommand(LoadCommand));

        // Review RUI-3: no command body throws today (they catch and return
        // error strings), but an unobserved ReactiveCommand exception crashes
        // the default handler - keep the class of failure closed.
        _cleanup.Add(LoadCommand.ThrownExceptions
            .Merge(AddCommand.ThrownExceptions)
            .Merge(DeleteCommand.ThrownExceptions)
            .Subscribe(e => LastError = e.Message));

        _isBusy = LoadCommand.IsExecuting
            .CombineLatest(AddCommand.IsExecuting, DeleteCommand.IsExecuting, (a, b, c) => a || b || c)
            .ToProperty(this, vm => vm.IsBusy);
        _cleanup.Add(_isBusy);

        // Live lease events, newest first, capped.
        _cleanup.Add(client.LeaseEvents.Subscribe(e => _events.Edit(list =>
        {
            list.Insert(0, e);

            if (list.Count > MaxEventRows)
            {
                list.RemoveAt(list.Count - 1);
            }
        })));

        _eventsRevision = _events.Connect()
            .Bind(out _eventRows)
            .Select((_, index) => index)
            .ToProperty(this, vm => vm.EventsRevision);
        _cleanup.Add(_eventsRevision);

        _cleanup.Add(_events);
    }

    /// <summary>Loads gateway info + the mapping table; re-runs after add/delete.</summary>
    public ReactiveCommand<Unit, (GatewayDto? Gateway, PortMappingDto[] Mappings)> LoadCommand { get; }

    /// <summary>Creates an auto-renewing mapping from the form fields.</summary>
    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    /// <summary>Deletes the given mapping row.</summary>
    public ReactiveCommand<PortMappingDto, Unit> DeleteCommand { get; }

    /// <summary>The gateway, or null while searching / when none answered.</summary>
    public GatewayDto? Gateway => _gateway.Value;

    /// <summary>The gateway's mapping table.</summary>
    public PortMappingDto[] Mappings => _mappings.Value;

    /// <summary>Newest-first feed of lease renewal events.</summary>
    public ReadOnlyObservableCollection<LeaseEventDto> EventRows => _eventRows;

    /// <summary>Bumps per event-feed change; drives re-rendering.</summary>
    public int EventsRevision => _eventsRevision.Value;

    /// <summary>Any command is running.</summary>
    public bool IsBusy => _isBusy.Value;

    /// <summary>Whether the add form is valid.</summary>
    public bool CanAdd => _canAdd.Value;

    public void Dispose() => _cleanup.Dispose();
}
