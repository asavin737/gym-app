namespace WebApi.Enrollments;

public sealed class EnrollmentCreateResult
{
    private EnrollmentCreateResult(int statusCode, string? reason, string? body)
    {
        StatusCode = statusCode;
        Reason = reason;
        Body = body;
    }

    public int StatusCode { get; }

    public string? Reason { get; }

    public string? Body { get; }

    public static EnrollmentCreateResult Created(string body) =>
        new(StatusCodes.Status201Created, null, body);

    public static EnrollmentCreateResult BadRequest() =>
        new(StatusCodes.Status400BadRequest, null, null);

    public static EnrollmentCreateResult NotFound() =>
        new(StatusCodes.Status404NotFound, null, null);

    public static EnrollmentCreateResult Conflict(string reason) =>
        new(StatusCodes.Status409Conflict, reason, null);
}
