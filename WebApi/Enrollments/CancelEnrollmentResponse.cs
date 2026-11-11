namespace WebApi.Enrollments;

public class CancelEnrollmentResponse
{
    public long Id { get; set; }

    public string Status { get; set; } = null!;

    public int RefundedVisits { get; set; }
}
