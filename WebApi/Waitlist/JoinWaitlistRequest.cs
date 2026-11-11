using System.Text.Json.Serialization;

namespace WebApi.Waitlist;

public class JoinWaitlistRequest
{
    [JsonRequired]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public long ClassId { get; set; }

    [JsonRequired]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int Seats { get; set; }
}
