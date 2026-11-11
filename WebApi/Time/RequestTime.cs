using System.Globalization;

namespace WebApi.Time;

public static class RequestTime
{
    public static bool TryParseInstant(string? value, out DateTime utc)
    {
        utc = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (HasOffset(value))
        {
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset))
            {
                return false;
            }

            utc = offset.UtcDateTime;
            return true;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    public static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(value) || value.Contains('T') || value.Contains(' '))
        {
            return false;
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool HasOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
        {
            return true;
        }

        var sign = value.LastIndexOfAny(['+', '-']);

        if (sign <= 0 || sign < value.IndexOf('T'))
        {
            return false;
        }

        var offset = value[(sign + 1)..];

        return offset.Length is 4 or 5 && offset.All(character => character is ':' or >= '0' and <= '9');
    }
}
