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

// Tell the user where to point the browser - including from other machines.
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine("UPnP.Rx dashboard is running. Open it in a browser:");

    foreach (var url in app.Urls)
    {
        Console.WriteLine($"  {url}");
    }

    var lanAddress = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus is System.Net.NetworkInformation.OperationalStatus.Up
            && nic.NetworkInterfaceType is not System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
        .Select(u => u.Address)
        .FirstOrDefault(a => a.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork);

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
