namespace WebApi.Enrollments;

public class EnrollmentCreatedResponse
{
    public long Id { get; set; }

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
