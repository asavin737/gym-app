namespace WebApi.Waitlist;

public sealed class WaitlistJoinResult
{
    private WaitlistJoinResult(int statusCode, string? reason, string? body)
    {
        StatusCode = statusCode;
        Reason = reason;
        Body = body;
    }

    public int StatusCode { get; }

    public string? Reason { get; }

    public string? Body { get; }

    public static WaitlistJoinResult Created(string body) => new(StatusCodes.Status201Created, null, body);

    public static WaitlistJoinResult BadRequest() => new(StatusCodes.Status400BadRequest, null, null);

    public static WaitlistJoinResult NotFound() => new(StatusCodes.Status404NotFound, null, null);

    public static WaitlistJoinResult Conflict(string reason) => new(StatusCodes.Status409Conflict, reason, null);
}
