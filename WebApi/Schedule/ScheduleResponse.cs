using System.Text.Json.Serialization;

namespace WebApi.Schedule;

public class ScheduleResponse
{
    public List<ScheduleItemResponse> Items { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ContinuationToken { get; set; }
}

public class ScheduleItemResponse
{
    public long ClassId { get; set; }

    public string Title { get; set; } = null!;

    public DateTime StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public ScheduleRoomResponse Room { get; set; } = null!;

    public int Capacity { get; set; }

    public int FreeSeats { get; set; }

    public int MySeats { get; set; }
}

public class ScheduleRoomResponse
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;
}
