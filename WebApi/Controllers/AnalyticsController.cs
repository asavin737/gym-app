using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Analytics;

namespace WebApi.Controllers;

[ApiController]
[Authorize(Roles = AdminRole)]
[Route("analytics")]
public class AnalyticsController : BaseApiController
{
    private readonly LoadService _loadService;

    public AnalyticsController(LoadService loadService)
    {
        _loadService = loadService;
    }

    [HttpGet("load")]
    public async Task<IActionResult> Load(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? roomId,
        CancellationToken cancellationToken)
    {
        var result = await _loadService.GetAsync(from, to, roomId, cancellationToken);

        if (result.Body is null)
        {
            return BadRequestProblem();
        }

        return Ok(result.Body);
    }
}
