namespace WebApi.Waitlist;

public class JoinWaitlistRequest
{
    public required long ClassId { get; set; }

    public required int Seats { get; set; }
}
