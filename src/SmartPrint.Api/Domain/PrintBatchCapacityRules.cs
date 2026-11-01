namespace SmartPrint.Api.Domain;

public static class PrintBatchCapacityRules
{
    public const int MaximumFiles = 20;
    public const long MaximumTotalBytes = 50L * 1024 * 1024;

    public static bool CanAdd(long existingFileCount, long existingBytes, long incomingBytes)
    {
        if (existingFileCount < 0) throw new ArgumentOutOfRangeException(nameof(existingFileCount));
        if (existingBytes < 0) throw new ArgumentOutOfRangeException(nameof(existingBytes));
        if (incomingBytes <= 0) throw new ArgumentOutOfRangeException(nameof(incomingBytes));

        return existingFileCount < MaximumFiles &&
            incomingBytes <= MaximumTotalBytes &&
            existingBytes <= MaximumTotalBytes - incomingBytes;
    }
}