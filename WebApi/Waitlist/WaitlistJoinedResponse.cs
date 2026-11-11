namespace WebApi.Waitlist;

public class WaitlistJoinedResponse
{
    public long Id { get; set; }

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public int Position { get; set; }

    public DateTime CreatedAt { get; set; }
}
