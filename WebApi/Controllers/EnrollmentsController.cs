using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Enrollments;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("enrollments")]
public class EnrollmentsController : BaseApiController
{
    private readonly EnrollmentService _enrollmentService;

    public EnrollmentsController(EnrollmentService enrollmentService)
    {
        _enrollmentService = enrollmentService;
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateEnrollmentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _enrollmentService.CreateAsync(
            CurrentUserId,
            idempotencyKey,
            request,
            cancellationToken);

        if (result.Body is not null)
        {
            return new ContentResult
            {
                StatusCode = StatusCodes.Status201Created,
                Content = result.Body,
                ContentType = "application/json"
            };
        }

        return result.StatusCode switch
        {
            StatusCodes.Status404NotFound => NotFoundProblem(),
            StatusCodes.Status409Conflict => ConflictProblem(result.Reason!),
            _ => BadRequestProblem()
        };
    }
}
