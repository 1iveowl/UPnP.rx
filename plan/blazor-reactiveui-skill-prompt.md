# Prompt: a skill for scaffolding Blazor WASM + ReactiveUI

*Copy everything below the line into the ai-skills project. It is written as a brief for
whoever builds the skill, in the same shape as the analyzer brief that worked well: what is
**settled** (measured, do not re-derive), what must be **verified first** (moves with
versions), the **pitfalls** that cost real time, and the **process**.*

*Provenance: every "settled" item was learned building `Sample.Dashboard` /
`Sample.Dashboard.Client` in [UPnP.Rx](https://github.com/1iveowl/UPnP.rx) — a Blazor WASM
client over a SignalR hub, ReactiveUI view models, DynamicData collections, FluentUI
components, on .NET 10. The deep review behind it is
`plan/upnp-rx-v4.0-rx-reactiveui-review.md` in that repo; several findings there became the
decisions below.*

---

## 0. What the skill is for, and what it is not

**Goal:** scaffold a Blazor WebAssembly solution using ReactiveUI for view models, so that
the first run works and the tenth feature does not need a rewrite. The value is almost
entirely in **getting a handful of non-obvious defaults right** — most of what follows
presents as *a blank page with a generic error banner*, which is the least debuggable
failure mode in the stack.

**Not for:** Blazor Server (see §5 — one assumption below is false there), MAUI/WPF
ReactiveUI (activation story differs), or teaching Rx. Assume the reader knows Rx.

**The single most transferable finding:**

> **On .NET 10, ReactiveUI's and Rx's own Blazor defaults are broken, and the failure is
> silent.** Rx 7.0.0's WASM scheduler enlightenment rejects the .NET 10 runtime, and
> anything that resolves the platform scheduler — including `ReactiveCommand`'s *default*
> output scheduler — dies in a static initializer. You get a blank page. Every command
> needs an explicit scheduler. A scaffold that omits this produces an app that builds,
> runs, and shows nothing.

---

## 1. Settled — copy this, do not re-derive it

### 1a. Bootstrap order (ReactiveUI 23.x)

`RxApp` statics are gone in 23. The builder must run **before anything calls
`WhenAnyValue`** — including field initializers in a view model resolved by DI.

```csharp
// Program.cs, FIRST, before WebAssemblyHostBuilder.CreateDefault
RxAppBuilder.CreateReactiveUIBuilder()
    .WithBlazorWasm()
    .BuildApp();
```

Getting this wrong kills the property-observation mixin in a static initializer: blank page,
generic Blazor error banner, nothing useful in the console.

### 1b. Every ReactiveCommand needs an explicit output scheduler

```csharp
// WASM is single-threaded: the current thread IS the UI thread.
private static readonly IScheduler _wasmSafe = CurrentThreadScheduler.Instance;

LoadCommand = ReactiveCommand.CreateFromTask(..., outputScheduler: _wasmSafe);
AddCommand  = ReactiveCommand.CreateFromTask(..., canExecute, _wasmSafe);
```

Same for any time-based operator: `Observable.Timer(..., DefaultScheduler.Instance)`.
Never let an operator resolve the platform scheduler on its own.

Note `WithBlazorWasm()` itself registers the scheduler that dies — the framework's own
Blazor default is unusable on .NET 10. That is not a reason to skip it (it registers the
rest of the platform services); it is a reason to override per command.

### 1c. The trimmer strips ReactiveUI on Release publish

ReactiveUI and Splat register services by reflection, so IL trimming removes them and the
app fails only in a published build — not in `dotnet run`.

```xml
<ItemGroup>
  <TrimmerRootAssembly Include="ReactiveUI" />
  <TrimmerRootAssembly Include="ReactiveUI.Blazor" />
  <TrimmerRootAssembly Include="Splat" />
</ItemGroup>
```

### 1d. Blazor does not observe collection changes — pump renders with a property

Blazor has no `INotifyCollectionChanged` binding. A `ReadOnlyObservableCollection` bound by
DynamicData will mutate and **nothing re-renders**. Count the changesets into a property
instead — one subscription does filter, sort, bind, and pump:

```csharp
_revision = client.Devices.Connect()
    .Filter(predicate)
    .SortAndBind(out _devices, SortExpressionComparer<DeviceDto>.Ascending(d => d.Name))
    .Select((_, index) => index)          // changeset counter…
    .ToProperty(this, vm => vm.Revision); // …whose notification re-renders

// The view binds @ViewModel.Revision somewhere, or simply reads the collection -
// the property change is what triggers the render.
```

This looks odd next to WPF-style binding and is the correct Blazor pattern. The
alternative — manual `Subscribe` + `StateHasChanged` in the component — trades a
declarative pipeline for imperative wiring and gains nothing. Use `SortAndBind`, not the
older `Sort().Bind()`.

### 1e. View models are transient, and the page disposes them

`ReactiveInjectableComponentBase<T>` disposes its injected view model when the component
unmounts. A **singleton view model comes back disposed on the next navigation**.

```csharp
builder.Services.AddSingleton<DeviceStreamClient>();   // shared state lives here
builder.Services.AddTransient<DashboardViewModel>();   // one per mount
```

```razor
@inherits ReactiveUI.Blazor.ReactiveInjectableComponentBase<DashboardViewModel>
@implements IDisposable
@code {
    void IDisposable.Dispose() => ViewModel?.Dispose();
}
```

Put cache/connection state in a singleton service so a fresh view model per mount loses
nothing.

### 1f. Lifecycle: constructor composition, not `WhenActivated`

Compose pipelines in the constructor into a `CompositeDisposable`, and dispose explicitly
(§1e). **This is a deliberate deviation from ReactiveUI canon** — `IActivatableViewModel` /
`WhenActivated` is the documented pattern — and it is defensible for Blazor WASM with
transient view models, where activation adds ceremony and its Blazor story is weaker than
XAML's. The skill should state it as a choice with a reason, not present it as the only way.

### 1g. Split the source generators from the hand-written parts

`ReactiveUI.SourceGenerators` (`PrivateAssets="all"`) for settable properties:

```csharp
[Reactive] private string _filter = string.Empty;
[Reactive(SetModifier = AccessModifier.Private)] private string? _lastError;
```

Keep **OAPHs and commands hand-written**: commands need the explicit scheduler (§1b), and
the OAPH pipelines document themselves better in full. Mixing deliberately is the point.

### 1h. Observe `ThrownExceptions` on every command

An unobserved `ReactiveCommand` exception takes down the default handler. Even when every
body catches and returns an error string today, that invariant is one refactor from false:

```csharp
_cleanup.Add(LoadCommand.ThrownExceptions
    .Merge(AddCommand.ThrownExceptions)
    .Merge(DeleteCommand.ThrownExceptions)
    .Subscribe(e => LastError = e.Message));
```

### 1i. Project-file settings that are not obvious

```xml
<PropertyGroup>
  <NoDefaultLaunchSettingsFile>true</NoDefaultLaunchSettingsFile>
  <StaticWebAssetProjectMode>Default</StaticWebAssetProjectMode>
  <!-- Navigation-away throws by default in .NET 8+; noisy in a WASM SPA. -->
  <BlazorDisableThrowNavigationException>true</BlazorDisableThrowNavigationException>
</PropertyGroup>
```

Host side (a hosted WASM app with a SignalR hub):

```csharp
builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
builder.Services.AddSignalR();
app.MapHub<DeviceHub>(HubEvents.Path);
app.MapRazorComponents<App>().AddInteractiveWebAssemblyRenderMode();
```

Put DTOs and hub method/route names in the **Client** project and reference it from the
host, so both ends share one definition and a rename is a compile error.

---

## 2. Verify first — these move

Do not copy version numbers; check them, and re-derive the item if the answer changed.

- **Is the Rx 7 / .NET 10 WASM scheduler defect fixed?** Everything in §1b exists because of
  it. Test: a `ReactiveCommand` with no explicit `outputScheduler` in a WASM app on the
  current runtime. If the page renders, the workaround can relax — but keep the explicit
  scheduler as house style anyway; it costs one argument.
- **ReactiveUI 24.** 23.2.28 was the last stable at the time of writing; 24 was in beta and
  **the `RxAppBuilder` bootstrap surface may move at the major**. Re-verify §1a before
  targeting 24.
- **`ReactiveUI.SourceGenerators`** attribute surface (`[Reactive]`, `[ObservableAsProperty]`)
  and whether OAPH generation has become good enough to drop the hand-written half of §1g.
- **DynamicData** — is `SortAndBind` still the current operator?
- **Component library.** FluentUI was the choice here; the skill should not hard-code it.
  Whatever it picks, note that `AddFluentUIComponents()`-style registration goes in
  `Program.cs` *after* the ReactiveUI builder.

---

## 3. Pitfalls that cost real time

1. **Blank page + generic error banner = a static initializer died.** Check, in order:
   bootstrap ran first (§1a), commands have explicit schedulers (§1b). Nothing in the
   browser console points at either.
2. **Works in `dotnet run`, broken when published** = the trimmer (§1c).
3. **Collection updates but the UI does not** = no render pump (§1d).
4. **Second navigation to a page throws `ObjectDisposedException`** = singleton view model
   with a component base that disposes it (§1e).
5. **Generated files inside the project directory get compiled twice.** If the scaffold
   emits anything with `EmitCompilerGeneratedFiles`, keep the output path under `obj/` —
   anywhere inside the project is picked up by the default glob *and* by the generator,
   giving CS0102 on every type. (Learned the hard way in the same repo, twice.)
6. **`ConfigureAwait(false)` is noise in WASM** — no synchronization context. If the
   solution shares an `.editorconfig` with a library that enforces CA2007, turn it off for
   the client project rather than littering the calls.

---

## 4. Testability — the part the scaffold should not skip

The pattern that made the sample testable is worth building in from the start: **the view
model depends on an interface, never on the transport.** `DeviceStreamClient` (SignalR)
sits behind a plain class exposing `IObservable<T>` and `Task<T>` members, so view models
are unit-testable with no connection. If the skill scaffolds a hub, it should scaffold that
seam with it.

Also worth a scaffolded example: **bounded** live feeds. A live event list needs a cap
(`SourceList.Edit` + trim) or it grows without limit on a page left open for a day.

---

## 5. The one thing that is *false* for Blazor Server

The sample mutates `BehaviorSubject`/`SourceCache` directly from SignalR callbacks without
synchronization. That is safe **only** because the SignalR client dispatches handlers
sequentially per connection *and* Blazor WASM is single-threaded — two guarantees that both
disappear on Blazor Server, where handlers ride the thread pool.

If the skill supports Server, or even just anticipates someone copying the code there, it
must either add `Synchronize`/`ObserveOn` or state the assumption loudly at the seam. This
was recorded as a review finding (RX-7) rather than fixed, precisely because the sample is
the thing people copy.

---

## 6. What this experience does *not* cover

Say so in the skill rather than inventing it: **authentication/authorization, prerendering
and the `InteractiveAuto` render mode, localization, and Blazor Server** were never
exercised. The `.NET 10` + `Rx 7` + `ReactiveUI 23` combination is the only one measured.

---

## 7. Deliverable

A skill that scaffolds a working Blazor WASM + ReactiveUI solution — host, client, a shared
DTO/hub-contract surface, one view model with a command and an OAPH, one DynamicData-bound
list with a render pump, and the project settings from §1i.

**It must produce something that runs on the first `dotnet run` and survives
`dotnet publish -c Release` with trimming.** Those two together catch §1a, §1b and §1c,
which is most of the value.

State every §1 item as a *reason*, not a rule. Someone who knows why the explicit scheduler
is there will keep it when they refactor; someone who copied it will delete it.
