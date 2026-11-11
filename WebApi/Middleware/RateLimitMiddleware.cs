using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace WebApi.Middleware;

public class RateLimitMiddleware
{
    private const int Limit = 60;
    private const long WindowMilliseconds = 60_000;

    // решение: окно скользящее, 60 секунд на пользователя. Фиксированная минута на стыке
    // пропустила бы вторую пачку из 60 запросов.
    private const string Script = """
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        local count = redis.call('ZCARD', KEYS[1])
        if count >= tonumber(ARGV[3]) then
            return count + 1
        end
        redis.call('ZADD', KEYS[1], now, ARGV[4])
        redis.call('PEXPIRE', KEYS[1], window)
        return count + 1
        """;

    private readonly RequestDelegate _next;
    private readonly IConnectionMultiplexer _connection;

    public RateLimitMiddleware(RequestDelegate next, IConnectionMultiplexer connection)
    {
        _next = next;
        _connection = connection;
    }

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        if (!IsLimited(context.Request)
            || !long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await _next(context);
            return;
        }

        var count = (long)await _connection.GetDatabase().ScriptEvaluateAsync(
            Script,
            [$"rate-limit:{userId}"],
            [
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                WindowMilliseconds,
                Limit,
                $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}"
            ]);

        if (count <= Limit)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests"
            }
        });
    }

    private static bool IsLimited(HttpRequest request)
    {
        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments is null || segments.Length == 0)
        {
            return false;
        }

        if (HttpMethods.IsGet(request.Method))
        {
            if (segments.Length == 1 && Equals(segments[0], "schedule"))
            {
                return true;
            }

            if (segments.Length == 2 && Equals(segments[0], "analytics") && Equals(segments[1], "load"))
            {
                return true;
            }

            return segments.Length == 2 && Equals(segments[0], "enrollments");
        }

        if (!HttpMethods.IsPost(request.Method) || !Equals(segments[0], "enrollments"))
        {
            return false;
        }

        return segments.Length switch
        {
            1 => true,
            2 => Equals(segments[1], "waitlist"),
            3 => Equals(segments[2], "seats") || Equals(segments[2], "cancel"),
            4 => Equals(segments[1], "waitlist") && Equals(segments[3], "cancel"),
            _ => false
        };
    }

    private static bool Equals(string segment, string expected)
    {
        return segment.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
