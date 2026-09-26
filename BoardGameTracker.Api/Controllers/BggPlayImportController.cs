using BoardGameTracker.Common;
using BoardGameTracker.Core.Games.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardGameTracker.Api.Controllers;

[ApiController]
[Route("api/bgg/plays")]
[Authorize(Roles = Constants.AuthRoles.UserOrAdmin)]
public class BggPlayImportController : ControllerBase
{
    private readonly IBggPlayImportService _playImportService;

    public BggPlayImportController(IBggPlayImportService playImportService)
    {
        _playImportService = playImportService;
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromQuery] string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return BadRequest("BGG username is required.");
        }

        var result = await _playImportService.ImportPlays(username.Trim());
        return Ok(result);
    }
}
