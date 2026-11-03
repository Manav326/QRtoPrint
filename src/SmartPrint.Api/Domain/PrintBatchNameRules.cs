namespace SmartPrint.Api.Domain;

public static class PrintBatchNameRules
{
    public const int MaximumNameLength = 50;

    public static string? Normalize(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0) return null;
        if (name.Length > MaximumNameLength || name.Any(char.IsControl))
            throw new ArgumentException($"A batch name must be 1–{MaximumNameLength} characters and cannot contain control characters.", nameof(value));
        return name;
    }

    public static string? Resolve(string? requestedName, IEnumerable<string?> existingNames)
    {
        var requested = Normalize(requestedName);
        var names = existingNames.Select(Normalize).Where(name => name is not null)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 1)
            throw new ArgumentException("The existing print batch has inconsistent names.", nameof(existingNames));
        if (requested is not null && names.Length == 1 && !string.Equals(requested, names[0], StringComparison.Ordinal))
            throw new ArgumentException("A print batch name cannot be changed after upload begins.", nameof(requestedName));
        return requested ?? names.FirstOrDefault();
    }
}
