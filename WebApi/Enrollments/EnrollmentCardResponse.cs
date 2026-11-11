namespace WebApi.Enrollments;

public class EnrollmentCardResponse
{
    public long Id { get; set; }

    public string Status { get; set; } = null!;

    public int Seats { get; set; }

    public DateTime CreatedAt { get; set; }

    public EnrollmentCardClass Class { get; set; } = null!;

    public EnrollmentCardPass Pass { get; set; } = null!;
}

public class EnrollmentCardClass
{
    public long Id { get; set; }

    public string Title { get; set; } = null!;

    public DateTime StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public bool IsCancelled { get; set; }

    public EnrollmentCardRoom Room { get; set; } = null!;
}

public class EnrollmentCardRoom
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;
}

public class EnrollmentCardPass
{
    public int RemainingVisits { get; set; }

    public DateOnly ValidUntil { get; set; }
}
