using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using Sample.Dashboard.Client.Services;
using Sample.Dashboard.Client.ViewModels;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddFluentUIComponents();
builder.Services.AddSingleton<DeviceStreamClient>();
// Transient, deliberately: ReactiveInjectableComponentBase disposes its
// injected view model when the component unmounts. A singleton would come back
// disposed on the next mount - the roster cache lives in DeviceStreamClient,
// so a fresh view model per mount loses nothing.
builder.Services.AddTransient<DashboardViewModel>();

await builder.Build().RunAsync();
