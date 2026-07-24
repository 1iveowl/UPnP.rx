using Sample.Dashboard.Components;
using Sample.Dashboard.Hubs;
using Sample.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

// The server does the SSDP listening; browsers get the roster over SignalR.
builder.Services.AddSignalR();
builder.Services.AddSingleton<DeviceRoster>();
builder.Services.AddHostedService<UpnpDiscoveryService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapHub<DeviceHub>("/devicehub");
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Sample.Dashboard.Client._Imports).Assembly);

app.Run();
