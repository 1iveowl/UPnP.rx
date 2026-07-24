using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using ReactiveUI.Builder;
using Sample.Dashboard.Client.Services;
using Sample.Dashboard.Client.ViewModels;

// ReactiveUI 23 requires explicit initialization via its builder BEFORE
// anything calls WhenAnyValue - otherwise the property-observation mixin dies
// in a static initializer (seen as a blank page + the generic Blazor error
// banner). WithBlazorWasm registers the WASM scheduler and platform services.
RxAppBuilder.CreateReactiveUIBuilder()
    .WithBlazorWasm()
    .BuildApp();

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddFluentUIComponents();
builder.Services.AddSingleton<DeviceStreamClient>();
// Transient, deliberately: ReactiveInjectableComponentBase disposes its
// injected view model when the component unmounts. A singleton would come back
// disposed on the next mount - the roster cache lives in DeviceStreamClient,
// so a fresh view model per mount loses nothing.
builder.Services.AddTransient<DashboardViewModel>();

await builder.Build().RunAsync();
