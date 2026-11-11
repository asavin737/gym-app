namespace DataAccessLayer.Queries;

public sealed class ScheduleRow
{
    public long ClassId { get; set; }

    public string Title { get; set; } = null!;

    public DateTime StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public long RoomId { get; set; }

    public string RoomName { get; set; } = null!;

    public int Capacity { get; set; }

    public int BookedSeats { get; set; }

    public int MySeats { get; set; }
}
