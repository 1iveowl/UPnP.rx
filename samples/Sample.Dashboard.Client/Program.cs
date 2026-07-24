using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using Sample.Dashboard.Client.Services;
using Sample.Dashboard.Client.ViewModels;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddFluentUIComponents();
builder.Services.AddSingleton<DeviceStreamClient>();
builder.Services.AddSingleton<DashboardViewModel>();

await builder.Build().RunAsync();
