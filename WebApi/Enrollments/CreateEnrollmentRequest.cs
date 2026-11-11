using System.Text.Json.Serialization;

namespace WebApi.Enrollments;

public class CreateEnrollmentRequest
{
    [JsonRequired]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public long ClassId { get; set; }

    [JsonRequired]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int Seats { get; set; }
}
