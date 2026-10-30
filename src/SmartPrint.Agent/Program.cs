using SmartPrint.Agent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "QRtoPrint Smart Print Agent";
});
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
