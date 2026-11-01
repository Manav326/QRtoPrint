using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartPrint.Api.Data;

/// <summary>
/// Keeps the repository's historical 12-digit migration IDs resolvable while
/// generating EF Core-compatible 14-digit IDs for all future migrations.
/// </summary>
public sealed class LegacyCompatibleMigrationsIdGenerator : IMigrationsIdGenerator
{
    private const string StandardTimestampFormat = "yyyyMMddHHmmss";
    private const int StandardTimestampLength = 14;
    private const int LegacyTimestampLength = 12;

    private readonly object _syncRoot = new();
    private DateTime _lastTimestamp = DateTime.MinValue;

    public string GenerateId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var now = DateTime.UtcNow;
        var timestamp = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

        lock (_syncRoot)
        {
            if (timestamp <= _lastTimestamp)
            {
                timestamp = _lastTimestamp.AddSeconds(1);
            }

            _lastTimestamp = timestamp;
        }

        return string.Concat(timestamp.ToString(StandardTimestampFormat, CultureInfo.InvariantCulture), "_", name);
    }

    public string GetName(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (HasTimestampPrefix(id, LegacyTimestampLength))
        {
            return id[(LegacyTimestampLength + 1)..];
        }

        if (HasTimestampPrefix(id, StandardTimestampLength))
        {
            return id[(StandardTimestampLength + 1)..];
        }

        throw new ArgumentException("The migration ID does not have a supported timestamp prefix.", nameof(id));
    }

    public bool IsValidId(string value) =>
        HasTimestampPrefix(value, StandardTimestampLength)
        || HasTimestampPrefix(value, LegacyTimestampLength);

    private static bool HasTimestampPrefix(string value, int timestampLength)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length <= timestampLength + 1
            || value[timestampLength] != '_')
        {
            return false;
        }

        for (var index = 0; index < timestampLength; index++)
        {
            if (!char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
