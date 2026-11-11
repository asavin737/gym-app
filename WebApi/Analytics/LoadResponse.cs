namespace WebApi.Analytics;

public class LoadResponse
{
    public List<LoadItemResponse> Items { get; set; } = [];
}

public class LoadItemResponse
{
    public DateOnly Date { get; set; }

    public LoadRoomResponse Room { get; set; } = null!;

    public int ClassesCount { get; set; }

    public int TotalCapacity { get; set; }

    public int BookedSeats { get; set; }
}

public class LoadRoomResponse
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;
}
