using System.Security.Cryptography;
using System.Text.Json;
using SmartPrint.Agent;
using SkiaSharp;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsPrinterDriverQualificationTests
{
    [Fact]
    public async Task ApprovedVendorDriverDiscoversCapabilitiesAndRendersToAnIsolatedFilePort()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var queueName = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_QUEUE");
        var outputPath = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_OUTPUT");
        var reportPath = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_REPORT");
        var driverId = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_ID");
        var driverName = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_DRIVER");

        Assert.False(string.IsNullOrWhiteSpace(queueName), "Qualification queue was not prepared.");
        Assert.False(string.IsNullOrWhiteSpace(outputPath), "Qualification file port was not prepared.");
        Assert.False(string.IsNullOrWhiteSpace(reportPath), "Qualification report path was not prepared.");
        Assert.False(string.IsNullOrWhiteSpace(driverId), "Approved driver ID was not supplied.");
        Assert.False(string.IsNullOrWhiteSpace(driverName), "Approved driver name was not supplied.");
        Assert.False(File.Exists(outputPath), "The isolated output path must not exist before printing.");

        using var bitmap = new SKBitmap(180, 240);
        bitmap.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = new SKColor(32, 96, 180), IsAntialias = true })
        {
            canvas.DrawRect(new SKRect(18, 18, 162, 222), paint);
            using var textPaint = new SKPaint { Color = SKColors.Black, TextSize = 12, IsAntialias = true };
            canvas.DrawText("SmartPrint driver QA", 20, 34, textPaint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(),
            "SP-DRIVER-QUALIFICATION",
            Guid.NewGuid(),
            queueName!,
            queueName!,
            "smartprint-driver-qualification.png",
            "image/png",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            1,
            1,
            "Colour",
            "A4",
            false,
            "INR",
            100,
            "SmartPrint-Driver-Qualification",
            "ACKNOWLEDGED",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.Physical);

        try
        {
            var receipt = new WindowsPrintExecutor().Print(entry, bytes, CancellationToken.None);
            Assert.True(receipt.JobId > 0, "Windows did not return a spooler job identifier.");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            byte[]? rendered = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(outputPath))
                    {
                        var candidate = await File.ReadAllBytesAsync(outputPath);
                        if (candidate.Length > 128)
                        {
                            rendered = candidate;
                            break;
                        }
                    }
                }
                catch (IOException)
                {
                    // The print processor may still be writing the output file.
                }

                await Task.Delay(500);
            }

            Assert.NotNull(rendered);
            Assert.True(rendered!.Length > 128, "The manufacturer driver produced no usable rendered spool output.");

            var capabilitiesJson = WindowsPrinterCapabilityDiscovery.DiscoverJson(queueName!);
            using var capabilities = JsonDocument.Parse(capabilitiesJson);
            Assert.Equal(1, capabilities.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("WINDOWS_DRIVER", capabilities.RootElement.GetProperty("source").GetString());

            var report = new
            {
                schemaVersion = 1,
                status = "DRIVER_RENDERED",
                driverId,
                driverName,
                queueName,
                spoolerJobId = receipt.JobId,
                outputFileName = Path.GetFileName(outputPath),
                outputSizeBytes = rendered.Length,
                outputSha256 = Convert.ToHexString(SHA256.HashData(rendered)).ToLowerInvariant(),
                outputHeaderHex = Convert.ToHexString(rendered.AsSpan(0, Math.Min(32, rendered.Length))),
                capabilities = capabilities.RootElement.Clone(),
                windowsVersion = Environment.OSVersion.Version.ToString(),
                qualificationScope = "SIGNED_DRIVER_INSTALL_AND_VIRTUAL_SPOOLER_RENDERING",
                physicalPrinterVerified = false,
                completedAtUtc = DateTimeOffset.UtcNow
            };

            Directory.CreateDirectory(Path.GetDirectoryName(reportPath!)!);
            await File.WriteAllTextAsync(reportPath!, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }
}
