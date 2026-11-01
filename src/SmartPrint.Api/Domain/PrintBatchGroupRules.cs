namespace SmartPrint.Api.Domain;

public static class PrintBatchGroupRules
{
    public const int MaximumNameLength = 80;
    public const string LooseFilesName = "Loose files";

    public static string Normalize(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return LooseFilesName;
        }

        if (name.Length > MaximumNameLength ||
            name is "." or ".." ||
            name.Contains('/') ||
            name.Contains('\\') ||
            name.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A folder name must be a single path segment of 1–{MaximumNameLength} characters.",
                nameof(value));
        }

        return name;
    }
}
