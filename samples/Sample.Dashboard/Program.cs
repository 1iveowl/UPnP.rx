using Microsoft.FluentUI.AspNetCore.Components;
using Sample.Dashboard.Components;
using Sample.Dashboard.Hubs;
using Sample.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

// FluentUI doc guidance (review F1): register on the server too, so any future
// server-rendered Fluent component finds its services.
builder.Services.AddFluentUIComponents();

// The server does the SSDP listening; browsers get the roster over SignalR.
builder.Services.AddSignalR();
builder.Services.AddSingleton<NetworkClientProvider>();
builder.Services.AddSingleton<DeviceRoster>();
builder.Services.AddSingleton<GatewayService>();
builder.Services.AddSingleton<UpnpDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpnpDiscoveryService>());

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

// No HTTPS redirection: this is a LAN dashboard, typically opened from other
// machines by plain http://<host-ip>:<port>; redirecting to a dev-cert HTTPS
// port would break that (and warns when no https port is configured).

app.UseAntiforgery();

app.MapStaticAssets();
app.MapHub<DeviceHub>(Sample.Dashboard.Client.Models.HubEvents.Path);
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Sample.Dashboard.Client._Imports).Assembly);

// Tell the user where to point the browser - including from other machines.
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine("UPnP.Rx dashboard is running. Open it in a browser:");

    foreach (var url in app.Urls)
    {
        Console.WriteLine($"  {url}");
    }

    var lanAddress = UPnP.Rx.LocalNetwork.IPv4Addresses().FirstOrDefault();

    var port = app.Urls
        .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null)
        .FirstOrDefault(u => u?.Scheme == Uri.UriSchemeHttp)?.Port;

    if (lanAddress is not null && port is not null)
    {
        Console.WriteLine($"  http://{lanAddress}:{port}  (from other machines on this network)");
    }

    Console.WriteLine();
});

app.Run();
