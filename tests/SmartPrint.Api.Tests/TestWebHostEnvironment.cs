using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace SmartPrint.Api.Tests;

internal sealed class TestWebHostEnvironment(string root, string environmentName) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "QRtoPrint.Tests";
    public string WebRootPath { get; set; } = root;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
