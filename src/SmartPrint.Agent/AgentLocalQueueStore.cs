using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SmartPrint.Agent;

public sealed class AgentLocalQueueStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QRtoPrint.Agent.LocalQueue.Document.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string queueRoot;
    private readonly bool applyWindowsAcl;

    public AgentLocalQueueStore(string? rootOverride = null)
    {
        queueRoot = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "QRtoPrint",
            "LocalQueue");
        applyWindowsAcl = rootOverride is null;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(queueRoot);
        if (applyWindowsAcl)
        {
            SecureDirectory(queueRoot);
        }

        foreach (var temporaryFile in Directory.EnumerateFiles(queueRoot, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            File.Delete(temporaryFile);
        }

        foreach (var documentFile in Directory.EnumerateFiles(queueRoot, "*.qdoc", SearchOption.TopDirectoryOnly))
        {
            var manifestPath = Path.ChangeExtension(documentFile, ".json");
            if (!File.Exists(manifestPath))
            {
                File.Delete(documentFile);
            }
        }

        await Task.CompletedTask;
    }

    public AgentLocalQueueEntry? Find(Guid jobId)
    {
        var manifestPath = GetManifestPath(jobId);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var json = File.ReadAllText(manifestPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<AgentLocalQueueEntry>(json, JsonOptions)
            ?? throw new InvalidDataException("A local print queue manifest is invalid.");
    }

    public IReadOnlyList<AgentLocalQueueEntry> GetPendingAcknowledgements()
    {
        var entries = new List<AgentLocalQueueEntry>();
        foreach (var path in Directory.EnumerateFiles(queueRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var entry = JsonSerializer.Deserialize<AgentLocalQueueEntry>(json, JsonOptions);
            if (entry is not null && entry.State == "ACK_PENDING")
            {
                entries.Add(entry);
            }
        }

        return entries.OrderBy(entry => entry.ReceivedUtc).ToArray();
    }

    public async Task<AgentLocalQueueEntry> EnqueueAsync(
        AgentClaimedJob job,
        ReadOnlyMemory<byte> documentBytes,
        CancellationToken cancellationToken)
    {
        if (job.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(job.JobNumber) ||
            documentBytes.Length != job.SizeBytes ||
            job.SizeBytes is <= 0 or > 20L * 1024 * 1024)
        {
            throw new InvalidDataException("The claimed job metadata or document size is invalid.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(documentBytes.Span)).ToLowerInvariant();
        if (!string.Equals(actualHash, job.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded document SHA-256 does not match the server's original.");
        }

        var existing = Find(job.Id);
        if (existing is not null)
        {
            if (!string.Equals(existing.Sha256, actualHash, StringComparison.OrdinalIgnoreCase) ||
                existing.SizeBytes != job.SizeBytes)
            {
                throw new InvalidDataException("A different document is already stored for this print job.");
            }

            if (!File.Exists(existing.DocumentPath))
            {
                throw new FileNotFoundException("The local queue manifest exists but its protected document is missing.", existing.DocumentPath);
            }

            return existing;
        }

        await InitializeAsync(cancellationToken);
        var documentPath = Path.Combine(queueRoot, job.Id.ToString("N") + ".qdoc");
        var temporaryDocumentPath = documentPath + ".tmp";
        if (File.Exists(documentPath))
        {
            File.Delete(documentPath);
        }

        var plaintext = documentBytes.ToArray();
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        try
        {
            await using (var stream = new FileStream(
                temporaryDocumentPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryDocumentPath, documentPath);
        }
        catch
        {
            if (File.Exists(temporaryDocumentPath)) File.Delete(temporaryDocumentPath);
            if (File.Exists(documentPath)) File.Delete(documentPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }

        var entry = new AgentLocalQueueEntry(
            job.Id,
            job.JobNumber,
            job.PrinterDeviceId,
            job.PrinterName,
            job.QueueName,
            job.DocumentName,
            job.MediaType,
            actualHash,
            job.SizeBytes,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.Currency,
            job.QuotedAmountMinor,
            documentPath,
            "ACK_PENDING",
            DateTimeOffset.UtcNow,
            job.LeaseExpiresUtc);

        try
        {
            await SaveManifestAsync(entry, cancellationToken);
            return entry;
        }
        catch
        {
            if (File.Exists(documentPath)) File.Delete(documentPath);
            throw;
        }
    }

    public async Task MarkAcknowledgedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var entry = Find(jobId);
        if (entry is null)
        {
            throw new InvalidOperationException("Cannot acknowledge a job that is not stored in the local queue.");
        }

        if (entry.State == "ACKNOWLEDGED")
        {
            return;
        }

        await SaveManifestAsync(entry with { State = "ACKNOWLEDGED" }, cancellationToken);
    }

    public byte[] ReadDocument(Guid jobId)
    {
        var entry = Find(jobId)
            ?? throw new FileNotFoundException("The print job is not present in the local queue.");
        var protectedBytes = File.ReadAllBytes(entry.DocumentPath);
        try
        {
            var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            var actualHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
            if (plaintext.LongLength != entry.SizeBytes ||
                !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new InvalidDataException("The protected local document failed its integrity check.");
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private async Task SaveManifestAsync(AgentLocalQueueEntry entry, CancellationToken cancellationToken)
    {
        var manifestPath = GetManifestPath(entry.JobId);
        var temporaryPath = manifestPath + ".tmp";
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (File.Exists(manifestPath))
        {
            File.Replace(temporaryPath, manifestPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, manifestPath);
        }
    }

    private string GetManifestPath(Guid jobId) =>
        Path.Combine(queueRoot, jobId.ToString("N") + ".json");

    private static void SecureDirectory(string directory)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administratorsSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
