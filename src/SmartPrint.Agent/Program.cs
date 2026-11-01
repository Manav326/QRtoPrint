using SmartPrint.Agent;

if (args.Any(argument => argument.Equals("--check-pairing", StringComparison.OrdinalIgnoreCase)))
{
    Environment.ExitCode = await AgentPairing.CheckExistingAsync();
    return;
}

if (args.Any(argument => argument.Equals("--pair", StringComparison.OrdinalIgnoreCase)))
{
    var pairingBuilder = Host.CreateApplicationBuilder(Array.Empty<string>());
    await AgentPairing.RunAsync(pairingBuilder.Configuration);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddSingleton<WindowsPrintExecutor>();
builder.Services.AddSingleton<LocalAgentApiState>();
builder.Services.AddHostedService<LocalAgentApiHostedService>();
builder.Services.AddHostedService<AgentControlPipeHostedService>();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
