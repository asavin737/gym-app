namespace WebApi.Enrollments;

public sealed class CancelEnrollmentResult
{
    private CancelEnrollmentResult(int statusCode, string? reason, CancelEnrollmentResponse? body)
    {
        StatusCode = statusCode;
        Reason = reason;
        Body = body;
    }

    public int StatusCode { get; }

    public string? Reason { get; }

    public CancelEnrollmentResponse? Body { get; }

    public static CancelEnrollmentResult NotFound() => new(StatusCodes.Status404NotFound, null, null);

    public static CancelEnrollmentResult Conflict(string reason) => new(StatusCodes.Status409Conflict, reason, null);

    public static CancelEnrollmentResult Ok(CancelEnrollmentResponse body) => new(StatusCodes.Status200OK, null, body);
}
