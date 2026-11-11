namespace WebApi.Waitlist;

public sealed class WaitlistLeaveResult
{
    private WaitlistLeaveResult(int statusCode, WaitlistLeftResponse? body)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }

    public WaitlistLeftResponse? Body { get; }

    public static WaitlistLeaveResult NotFound() => new(StatusCodes.Status404NotFound, null);

    public static WaitlistLeaveResult Ok(WaitlistLeftResponse body) => new(StatusCodes.Status200OK, body);
}
