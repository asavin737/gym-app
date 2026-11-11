using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Schedule;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("schedule")]
public class ScheduleController : BaseApiController
{
    private readonly ScheduleService _scheduleService;

    public ScheduleController(ScheduleService scheduleService)
    {
        _scheduleService = scheduleService;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? roomId,
        [FromQuery] string? limit,
        [FromQuery] string? continuationToken,
        CancellationToken cancellationToken)
    {
        var result = await _scheduleService.GetAsync(
            CurrentUserId,
            from,
            to,
            roomId,
            limit,
            continuationToken,
            cancellationToken);

        if (result.Body is null)
        {
            return BadRequestProblem();
        }

        return Ok(result.Body);
    }
}
