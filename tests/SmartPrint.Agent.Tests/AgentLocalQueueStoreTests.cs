using System.Security.Cryptography;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentLocalQueueStoreTests
{
    [Fact]
    public async Task PersistsTheDocumentEncryptedAndAcknowledgesIdempotently()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var content = "Private print document bytes"u8.ToArray();
            var job = CreateJob(content);

            var entry = await store.EnqueueAsync(job, content, CancellationToken.None);
            var encryptedOnDisk = await File.ReadAllBytesAsync(entry.DocumentPath);

            Assert.False(content.AsSpan().SequenceEqual(encryptedOnDisk));
            Assert.Equal("ACK_PENDING", entry.State);
            Assert.True(content.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
            Assert.Single(store.GetPendingAcknowledgements());

            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", store.Find(job.Id)!.State);
            Assert.Empty(store.GetPendingAcknowledgements());

            var duplicate = await store.EnqueueAsync(job, content, CancellationToken.None);
            Assert.Equal(job.Id, duplicate.JobId);
            Assert.Equal("ACKNOWLEDGED", duplicate.State);
            Assert.Single(Directory.GetFiles(root, "*.qdoc"));
            Assert.Single(Directory.GetFiles(root, "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsChangedContentForAnExistingJobId()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var firstContent = "Original bytes"u8.ToArray();
            var firstJob = CreateJob(firstContent);
            await store.EnqueueAsync(firstJob, firstContent, CancellationToken.None);

            var changedContent = "Different bytes"u8.ToArray();
            var changedJob = CreateJob(changedContent) with { Id = firstJob.Id };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.EnqueueAsync(changedJob, changedContent, CancellationToken.None));
            Assert.True(firstContent.AsSpan().SequenceEqual(store.ReadDocument(firstJob.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsAHashMismatchBeforeWritingAnything()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var content = "Untrusted bytes"u8.ToArray();
            var job = CreateJob("Different bytes"u8.ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.EnqueueAsync(job, content, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentClaimedJob CreateJob(byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return new AgentClaimedJob(
            Guid.NewGuid(),
            "SP-261030-TEST0001",
            "LOCAL_QUEUE_CLAIMED",
            1,
            1,
            "Mono",
            "A4",
            false,
            "INR",
            200,
            Guid.NewGuid(),
            "Shop Printer",
            "Microsoft Print to PDF",
            "receipt.pdf",
            "application/pdf",
            content.LongLength,
            hash,
            DateTimeOffset.UtcNow.AddMinutes(3));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "qrtoprint-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
