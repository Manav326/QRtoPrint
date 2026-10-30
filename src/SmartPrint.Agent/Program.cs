using SmartPrint.Agent;

var builder = Host.CreateApplicationBuilder(args);

if (args.Any(argument => argument.Equals("--pair", StringComparison.OrdinalIgnoreCase)))
{
    await AgentPairing.RunAsync(builder.Configuration);
    return;
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "QRtoPrint Smart Print Agent";
});
builder.Services.AddHttpClient("cloud", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<AgentCredentialStore>();
builder.Services.AddSingleton<WindowsPrinterDiscovery>();
builder.Services.AddSingleton<AgentLocalQueueStore>();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
