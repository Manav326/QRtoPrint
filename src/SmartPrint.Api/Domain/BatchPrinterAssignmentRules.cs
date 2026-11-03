namespace SmartPrint.Api.Domain;

public sealed record BatchPrinterAssignmentState(
    Guid? PrinterDeviceId,
    Guid? LocalAgentDeviceId,
    bool IsLocalAgentDocument,
    bool IsOutputLocked,
    bool HasActiveAgentLease);

public static class BatchPrinterAssignmentRules
{
    public static string? FindConflict(
        IEnumerable<BatchPrinterAssignmentState> jobs,
        Guid targetPrinterId,
        Guid targetAgentId)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (targetPrinterId == Guid.Empty || targetAgentId == Guid.Empty)
            throw new ArgumentException("A valid target printer and Agent are required.");

        var batch = jobs.ToArray();
        if (batch.Length == 0)
            return "batch_empty";

        if (batch.Any(job =>
            job.IsLocalAgentDocument &&
            job.LocalAgentDeviceId != targetAgentId))
        {
            return "local_document_agent_locked";
        }

        if (batch.Any(job =>
            (job.IsOutputLocked || job.HasActiveAgentLease) &&
            job.PrinterDeviceId != targetPrinterId))
        {
            return "batch_printer_locked";
        }

        return null;
    }
}
