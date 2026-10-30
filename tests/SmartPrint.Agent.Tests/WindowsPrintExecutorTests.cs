using System.Security.Cryptography;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsPrintExecutorTests
{
    [Fact]
    public void RejectsUnsupportedDocumentBeforeOpeningAnyPrinter()
    {
        var bytes = "plain text"u8.ToArray();
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-TEST-PRINT", Guid.NewGuid(), "Test printer", "Missing Windows queue",
            "test.txt", "text/plain", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength, 1, 1, "Mono", "A4", false, "INR", 100, "unused", "ACKNOWLEDGED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3));

        var exception = Assert.Throws<PrintExecutionBlockedException>(() =>
            new WindowsPrintExecutor().Print(entry, bytes, CancellationToken.None));

        Assert.Equal("DOCUMENT_TYPE_UNSUPPORTED", exception.ReasonCode);
    }

    [Fact]
    public void RejectsChangedBytesBeforePrinterAccess()
    {
        var bytes = "tampered document"u8.ToArray();
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-TEST-HASH", Guid.NewGuid(), "Test printer", "Missing Windows queue",
            "test.pdf", "application/pdf", new string('0', 64), bytes.LongLength, 1, 1, "Mono", "A4",
            false, "INR", 100, "unused", "ACKNOWLEDGED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3));

        var exception = Assert.Throws<PrintExecutionBlockedException>(() =>
            new WindowsPrintExecutor().Print(entry, bytes, CancellationToken.None));

        Assert.Equal("LOCAL_DOCUMENT_INTEGRITY_MISMATCH", exception.ReasonCode);
    }
}
