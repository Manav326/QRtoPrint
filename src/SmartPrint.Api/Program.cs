var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.MapHealthChecks("/health/ready");
app.MapGet("/health/live", () => Results.Ok(new
{
    service = "qrtoprint-api",
    status = "live",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0"
}));

app.MapGet("/", () => Results.Ok(new
{
    product = "QRtoPrint",
    name = "Smart Print API",
    apiVersion = "v1",
    health = "/health/live"
}));

app.Run();

public partial class Program;
