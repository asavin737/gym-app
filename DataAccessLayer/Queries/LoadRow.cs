namespace DataAccessLayer.Queries;

public sealed class LoadRow
{
    public DateOnly Date { get; set; }

    public long RoomId { get; set; }

    public string RoomName { get; set; } = null!;

    public int ClassesCount { get; set; }

    public int TotalCapacity { get; set; }

    public int BookedSeats { get; set; }
}
