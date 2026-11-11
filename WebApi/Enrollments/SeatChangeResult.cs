namespace WebApi.Enrollments;

public sealed class SeatChangeResult
{
    private SeatChangeResult(int statusCode, string? reason, EnrollmentCardResponse? card, bool changed)
    {
        StatusCode = statusCode;
        Reason = reason;
        Card = card;
        Changed = changed;
    }

    public int StatusCode { get; }

    public string? Reason { get; }

    public EnrollmentCardResponse? Card { get; }

    public bool Changed { get; }

    public static SeatChangeResult BadRequest() => new(StatusCodes.Status400BadRequest, null, null, false);

    public static SeatChangeResult NotFound() => new(StatusCodes.Status404NotFound, null, null, false);

    public static SeatChangeResult Conflict(string reason) => new(StatusCodes.Status409Conflict, reason, null, false);

    public static SeatChangeResult Ok(EnrollmentCardResponse card, bool changed) =>
        new(StatusCodes.Status200OK, null, card, changed);
}
