using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Enrollments;
using WebApi.Routing;
using WebApi.Waitlist;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("enrollments")]
public class EnrollmentsController : BaseApiController
{
    private readonly EnrollmentService _enrollmentService;
    private readonly WaitlistService _waitlistService;

    public EnrollmentsController(EnrollmentService enrollmentService, WaitlistService waitlistService)
    {
        _enrollmentService = enrollmentService;
        _waitlistService = waitlistService;
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

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!RouteId.TryParse(id, out var enrollmentId))
        {
            return BadRequestProblem();
        }

        var card = await _enrollmentService.GetAsync(CurrentUserId, enrollmentId, cancellationToken);

        if (card is null)
        {
            return NotFoundProblem();
        }

        return Ok(card);
    }

    [HttpPost("{id}/seats")]
    public async Task<IActionResult> ChangeSeats(
        string id,
        [FromBody] ChangeSeatsRequest request,
        CancellationToken cancellationToken)
    {
        if (!RouteId.TryParse(id, out var enrollmentId))
        {
            return BadRequestProblem();
        }

        var result = await _enrollmentService.ChangeSeatsAsync(CurrentUserId, enrollmentId, request, cancellationToken);

        if (result.Card is not null)
        {
            return Ok(result.Card);
        }

        return result.StatusCode switch
        {
            StatusCodes.Status404NotFound => NotFoundProblem(),
            StatusCodes.Status409Conflict => ConflictProblem(result.Reason!),
            _ => BadRequestProblem()
        };
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(string id, CancellationToken cancellationToken)
    {
        if (!RouteId.TryParse(id, out var enrollmentId))
        {
            return BadRequestProblem();
        }

        var result = await _enrollmentService.CancelAsync(CurrentUserId, enrollmentId, cancellationToken);

        if (result.Body is not null)
        {
            return Ok(result.Body);
        }

        return result.StatusCode switch
        {
            StatusCodes.Status404NotFound => NotFoundProblem(),
            _ => ConflictProblem(result.Reason!)
        };
    }

    [HttpPost("waitlist")]
    public async Task<IActionResult> JoinWaitlist(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] JoinWaitlistRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _waitlistService.JoinAsync(CurrentUserId, idempotencyKey, request, cancellationToken);

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

    [HttpPost("waitlist/{id}/cancel")]
    public async Task<IActionResult> LeaveWaitlist(string id, CancellationToken cancellationToken)
    {
        if (!RouteId.TryParse(id, out var entryId))
        {
            return BadRequestProblem();
        }

        var result = await _waitlistService.LeaveAsync(CurrentUserId, entryId, cancellationToken);

        if (result.Body is null)
        {
            return NotFoundProblem();
        }

        return Ok(result.Body);
    }
}
