using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = "admin";

    protected long CurrentUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    protected bool IsAdmin => User.IsInRole(AdminRole);

    protected ObjectResult BadRequestProblem() => CreateProblem(StatusCodes.Status400BadRequest);

    protected ObjectResult ForbiddenProblem() => CreateProblem(StatusCodes.Status403Forbidden);

    protected ObjectResult NotFoundProblem() => CreateProblem(StatusCodes.Status404NotFound);

    protected ObjectResult ConflictProblem(string reason) => CreateProblem(StatusCodes.Status409Conflict, reason);

    protected ObjectResult TooManyRequestsProblem() => CreateProblem(StatusCodes.Status429TooManyRequests);

    private ObjectResult CreateProblem(int statusCode, string? reason = null)
    {
        // решение: title — стандартное имя статуса, код контракта лежит в reason.
        var title = statusCode switch
        {
            StatusCodes.Status400BadRequest => "Bad Request",
            StatusCodes.Status403Forbidden => "Forbidden",
            StatusCodes.Status404NotFound => "Not Found",
            StatusCodes.Status409Conflict => "Conflict",
            StatusCodes.Status429TooManyRequests => "Too Many Requests",
            _ => "Error"
        };

        var details = new ProblemDetails
        {
            Status = statusCode,
            Title = title
        };

        if (reason is not null)
        {
            details.Extensions["reason"] = reason;
        }

        return new ObjectResult(details)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" }
        };
    }
}
