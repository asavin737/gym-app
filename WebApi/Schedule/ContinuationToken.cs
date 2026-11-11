using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace WebApi.Schedule;

public static class ContinuationToken
{
    public static string Create(DateTime startsAt, long classId)
    {
        // решение: токен — Base64Url из ticks начала и id занятия.
        // При равном startsAt страница продолжается по id, битая строка даёт 400.
        var utc = startsAt.Kind == DateTimeKind.Local
            ? startsAt.ToUniversalTime()
            : DateTime.SpecifyKind(startsAt, DateTimeKind.Utc);
        var payload = Encoding.ASCII.GetBytes($"{utc.Ticks}:{classId}");
        return Base64Url.EncodeToString(payload);
    }

    public static bool TryParse(string? value, out DateTime startsAt, out long classId)
    {
        startsAt = default;
        classId = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        byte[] bytes;

        try
        {
            bytes = Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = Encoding.ASCII.GetString(bytes).Split(':');

        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out classId)
            || ticks < DateTime.MinValue.Ticks
            || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        startsAt = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }
}
