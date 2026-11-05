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
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_DRIVER_QUALIFICATION"),
                "1",
                StringComparison.Ordinal))
            return;

        Assert.True(OperatingSystem.IsWindows(), "Driver qualification requires a Windows runner.");

        var queueName = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_QUEUE");
        var outputPath = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_OUTPUT");
        var reportPath = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_REPORT");
        var driverId = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_ID");
        var driverName = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_DRIVER");
        var driverVersion = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_VERSION");
        var driverManufacturer = Environment.GetEnvironmentVariable("SMARTPRINT_DRIVER_QUALIFICATION_MANUFACTURER");

        Assert.False(string.IsNullOrWhiteSpace(queueName), "Qualification queue was not prepared.");
        Assert.False(string.IsNullOrWhiteSpace(outputPath), "Qualification file port was not prepared.");
        Assert.False(string.IsNullOrWhiteSpace(reportPath), "Qualification report path was not prepared.");
        Assert.False(string.IsNullOrWhiteSpace(driverId), "Approved driver ID was not supplied.");
        Assert.False(string.IsNullOrWhiteSpace(driverName), "Approved driver name was not supplied.");
        Assert.False(string.IsNullOrWhiteSpace(driverVersion), "Installed driver version was not supplied.");
        Assert.False(string.IsNullOrWhiteSpace(driverManufacturer), "Installed driver manufacturer was not supplied.");
        Assert.False(File.Exists(outputPath), "The isolated output path must not exist before printing.");

        using var bitmap = new SKBitmap(180, 240);
        bitmap.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = new SKColor(32, 96, 180), IsAntialias = true })
        {
            canvas.DrawRect(new SKRect(18, 18, 162, 222), paint);
            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
            using var textFont = new SKFont(typeface, 12);
            using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText("SmartPrint driver QA", 20, 34, SKTextAlign.Left, textFont, textPaint);
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
            "Mono",
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
            var executor = new WindowsPrintExecutor();
            var receipt = executor.Print(entry, bytes, CancellationToken.None);
            Assert.True(receipt.JobId > 0, "Windows did not return a spooler job identifier.");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            byte[]? rendered = null;
            string? previousOutputHash = null;
            var stableOutputObservations = 0;
            var spoolerFinished = false;
            var finalSpoolerStatus = "UNKNOWN";
            int? finalPagesPrinted = null;
            int? finalTotalPages = null;
            while (DateTime.UtcNow < deadline)
            {
                var currentJob = executor.GetJob(queueName!, receipt.JobId);
                finalSpoolerStatus = currentJob?.Status ?? "REMOVED";
                finalPagesPrinted = currentJob?.PagesPrinted;
                finalTotalPages = currentJob?.TotalPages;

                if (currentJob?.Status is "OFFLINE" or "PAPEROUT" or "PAUSED" or "ERROR" or "USERINTERVENTION")
                    throw new InvalidOperationException($"Windows spooler reported driver job state '{currentJob.Status}'.");

                spoolerFinished = currentJob is null ||
                    (currentJob.Status == "PRINTED" &&
                     currentJob.TotalPages > 0 &&
                     currentJob.PagesPrinted >= currentJob.TotalPages);

                try
                {
                    if (File.Exists(outputPath))
                    {
                        var candidate = await File.ReadAllBytesAsync(outputPath!);
                        if (candidate.Length > 128)
                        {
                            var candidateHash = Convert.ToHexString(SHA256.HashData(candidate));
                            stableOutputObservations = string.Equals(candidateHash, previousOutputHash, StringComparison.Ordinal)
                                ? stableOutputObservations + 1
                                : 1;
                            previousOutputHash = candidateHash;

                            // A completed spooler job plus three identical half-second reads
                            // avoids treating a still-growing file as successful output.
                            if (spoolerFinished && stableOutputObservations >= 3)
                            {
                                rendered = candidate;
                                break;
                            }
                        }
                        else
                        {
                            previousOutputHash = null;
                            stableOutputObservations = 0;
                        }
                    }
                }
                catch (IOException)
                {
                    // The print processor may still be writing the output file.
                    previousOutputHash = null;
                    stableOutputObservations = 0;
                }

                await Task.Delay(500);
            }

            Assert.NotNull(rendered);
            Assert.True(rendered!.Length > 128, "The driver produced no usable rendered spool output.");
            Assert.Contains(rendered!, value => value != 0);

            var capabilitiesJson = WindowsPrinterCapabilityDiscovery.DiscoverJson(queueName!);
            using var capabilities = JsonDocument.Parse(capabilitiesJson);
            Assert.Equal(1, capabilities.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("WINDOWS_DRIVER", capabilities.RootElement.GetProperty("source").GetString());
            var paperSizes = capabilities.RootElement.GetProperty("paperSizes");
            Assert.Equal(JsonValueKind.Array, paperSizes.ValueKind);
            Assert.True(
                paperSizes.EnumerateArray().Any(paper =>
                    string.Equals(paper.GetProperty("name").GetString(), "A4", StringComparison.OrdinalIgnoreCase)),
                "The India-market qualification baseline requires the driver to report A4 paper support.");

            var report = new
            {
                schemaVersion = 1,
                status = "DRIVER_RENDERED",
                driverId,
                driverName,
                driverVersion,
                driverManufacturer,
                queueName,
                spoolerJobId = receipt.JobId,
                spoolerStatusAtCompletion = finalSpoolerStatus,
                spoolerPagesPrinted = finalPagesPrinted,
                spoolerTotalPages = finalTotalPages,
                stableOutputObservations,
                outputFileName = Path.GetFileName(outputPath!),
                outputSizeBytes = rendered.Length,
                outputSha256 = Convert.ToHexString(SHA256.HashData(rendered)).ToLowerInvariant(),
                outputHeaderHex = Convert.ToHexString(rendered.AsSpan(0, Math.Min(32, rendered.Length))),
                capabilities = capabilities.RootElement.Clone(),
                windowsVersion = Environment.OSVersion.Version.ToString(),
                windowsPlatform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
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
                File.Delete(outputPath!);
        }
    }
}
