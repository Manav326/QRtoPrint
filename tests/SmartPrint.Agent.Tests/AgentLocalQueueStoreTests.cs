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
    public async Task LocalUploadIsStagedUntilPaidJobClaimUpdatesItsPrintSettings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Local-only customer document"u8.ToArray();
            var upload = CreateJob(bytes) with
            {
                Status = "LOCAL_UPLOAD_STAGED",
                StorageMode = "LOCAL_AGENT",
                Copies = 1,
                ColourMode = "Mono",
                IsDuplex = false
            };

            var staged = await store.EnqueueLocalUploadAsync(upload, bytes, CancellationToken.None);

            Assert.Equal("LOCAL_UPLOAD_STAGED", staged.State);
            Assert.Empty(store.GetAcknowledgedJobs());
            Assert.Empty(store.GetPendingAcknowledgements());
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(upload.Id)));

            var paidClaim = upload with
            {
                Status = "LOCAL_QUEUE_CLAIMED",
                Copies = 3,
                ColourMode = "Colour",
                IsDuplex = true,
                QuotedAmountMinor = 900,
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(3)
            };
            await store.UpdateFromClaimAsync(paidClaim, CancellationToken.None);

            var refreshed = store.Find(upload.Id)!;
            Assert.Equal("ACK_PENDING", refreshed.State);
            Assert.Single(store.GetPendingAcknowledgements());
            Assert.Equal(3, refreshed.Copies);
            Assert.Equal("Colour", refreshed.ColourMode);
            Assert.True(refreshed.IsDuplex);
            Assert.Equal(900, refreshed.QuotedAmountMinor);
            Assert.Empty(store.GetAcknowledgedJobs());

            await store.MarkAcknowledgedAsync(upload.Id, CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", store.Find(upload.Id)!.State);
            Assert.Single(store.GetAcknowledgedJobs());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalUploadCannotBeClaimedByADifferentPrinter()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Printer-bound local document"u8.ToArray();
            var upload = CreateJob(bytes) with { StorageMode = "LOCAL_AGENT" };
            await store.EnqueueLocalUploadAsync(upload, bytes, CancellationToken.None);

            var changedAssignment = upload with
            {
                PrinterDeviceId = Guid.NewGuid(),
                QueueName = "Different Windows Queue"
            };

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.UpdateFromClaimAsync(changedAssignment, CancellationToken.None));
            Assert.Equal("LOCAL_UPLOAD_STAGED", store.Find(upload.Id)!.State);
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(upload.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizedLocalUploadsSurviveTicketExpiryCleanup()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Finalized local document"u8.ToArray();
            var job = CreateJob(bytes) with
            {
                StorageMode = "LOCAL_AGENT",
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await store.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);

            await store.MarkLocalUploadFinalizedAsync(job.Id, CancellationToken.None);
            Assert.Equal("LOCAL_UPLOAD_PENDING_PAYMENT", store.Find(job.Id)!.State);
            Assert.Empty(store.GetStagedLocalUploads());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.DeleteExpiredLocalUploadAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeletesOnlyExpiredUnfinalizedLocalUploads()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Expired local upload"u8.ToArray();
            var job = CreateJob(bytes) with
            {
                StorageMode = "LOCAL_AGENT",
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var entry = await store.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);

            await store.DeleteExpiredLocalUploadAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Null(store.Find(job.Id));
            Assert.False(File.Exists(entry.DocumentPath));
            Assert.Empty(Directory.GetFiles(root, "*.json"));
            Assert.Empty(Directory.GetFiles(root, "*.qdoc"));
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


    [Fact]
    public async Task SpoolerSubmissionTransitionsAreDurableAndNotAutomaticallyRequeued()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "One source document"u8.ToArray();
            var job = CreateJob(bytes);
            await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            Assert.Single(store.GetAcknowledgedJobs());

            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-Test-StableName", CancellationToken.None);
            Assert.Equal("SUBMITTING", store.Find(job.Id)!.State);
            Assert.Single(store.GetInterruptedSubmissions());
            Assert.Empty(store.GetAcknowledgedJobs());

            await store.MarkSpoolerAcceptedAsync(job.Id, 123, "SmartPrint-Test-StableName", CancellationToken.None);
            Assert.Equal("SPOOLER_ACCEPTED", store.Find(job.Id)!.State);
            Assert.Single(store.GetSpoolerTrackingJobs());
            Assert.Empty(store.GetInterruptedSubmissions());

            await store.UpdateSpoolerStatusAsync(job.Id, "PRINTING", CancellationToken.None);
            Assert.Equal("PRINTING", store.Find(job.Id)!.SpoolerStatus);
            await store.MarkOutputUnconfirmedAsync(job.Id, "SPOOLER_JOB_NO_LONGER_VISIBLE", CancellationToken.None);
            Assert.Equal("OUTPUT_UNCONFIRMED", store.Find(job.Id)!.State);
            Assert.Empty(store.GetAcknowledgedJobs());
            Assert.Empty(store.GetSpoolerTrackingJobs());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PreSubmissionBlockReturnsJobToAcknowledgedStateForExplicitRetry()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Retry-safe document"u8.ToArray();
            var job = CreateJob(bytes);
            await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-Test-Retry", CancellationToken.None);
            await store.MarkReadyAfterBlockedAsync(job.Id, "PRINTER_OFFLINE", CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", store.Find(job.Id)!.State);
            Assert.Single(store.GetAcknowledgedJobs());
            Assert.Empty(store.GetInterruptedSubmissions());
        }
        finally { Directory.Delete(root, recursive: true); }
    }


    [Fact]
    public async Task ShopOutputConfirmationStopsLocalSpoolerMonitoring()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Output checked"u8.ToArray();
            var job = CreateJob(bytes);
            await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-OutputChecked", CancellationToken.None);
            await store.MarkSpoolerAcceptedAsync(job.Id, 456, "SmartPrint-OutputChecked", CancellationToken.None);
            await store.MarkOutputConfirmedAsync(job.Id, CancellationToken.None);

            Assert.Equal("OUTPUT_CONFIRMED", store.Find(job.Id)!.State);
            Assert.Empty(store.GetSpoolerTrackingJobs());
        }
        finally { Directory.Delete(root, recursive: true); }
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
