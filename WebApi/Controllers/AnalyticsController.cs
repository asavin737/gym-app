using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

[ApiController]
[Authorize(Roles = AdminRole)]
[Route("analytics")]
public class AnalyticsController : BaseApiController
{
}
