namespace WebApi.Enrollments;

public class CreateEnrollmentRequest
{
    public required long ClassId { get; set; }

    public required int Seats { get; set; }
}
