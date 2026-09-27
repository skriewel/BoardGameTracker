using System.Security.Cryptography;
using System.Text;
using BoardGameTracker.Core.Dashboard.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardGameTracker.Api.Controllers;

[ApiController]
[Route("api/v1/stats")]
[AllowAnonymous]
public class HomepageStatsController : ControllerBase
{
    private const string TokenEnvironmentVariable = "HOMEPAGE_API_TOKEN";
    private readonly IDashboardService _dashboardService;

    public HomepageStatsController(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    [HttpGet]
    public async Task<IActionResult> GetStats()
    {
        if (!HasValidToken())
        {
            return Unauthorized();
        }

        var statistics = await _dashboardService.GetStatistics();

        return Ok(new
        {
            games = statistics.TotalGames,
            players = statistics.ActivePlayers,
            sessions = statistics.SessionsPlayed,
            collectionValue = statistics.TotalCollectionValue
        });
    }

    private bool HasValidToken()
    {
        var expected = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var authorization = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = authorization[prefix.Length..].Trim();

        if (provided.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }
}
