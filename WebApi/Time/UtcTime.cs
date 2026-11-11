namespace WebApi.Time;

public static class UtcTime
{
    public static DateTime Truncate(DateTime value)
    {
        // решение: Postgres хранит timestamptz с точностью до микросекунды,
        // иначе createdAt из ответа и из повторного чтения расходятся.
        var ticks = value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond);

        return new DateTime(ticks, DateTimeKind.Utc);
    }
}
