using System.Security.Cryptography;
using System.Text;
using BoardGameTracker.Core.Dashboard.Interfaces;
using BoardGameTracker.Core.GameNights.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace BoardGameTracker.Api.Controllers;

[ApiController]
[Route("api/v1/stats")]
[AllowAnonymous]
public class HomepageStatsController : ControllerBase
{
    private const string TokenConfigurationKey = "HOMEPAGE_API_TOKEN";
    private readonly IDashboardService _dashboardService;
    private readonly IGameNightService _gameNightService;
    private readonly IConfiguration _configuration;

    public HomepageStatsController(
        IDashboardService dashboardService,
        IGameNightService gameNightService,
        IConfiguration configuration)
    {
        _dashboardService = dashboardService;
        _gameNightService = gameNightService;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> GetStats()
    {
        if (!HasValidToken())
        {
            return Unauthorized();
        }

        var statistics = await _dashboardService.GetStatistics();
        var upcomingGameNights = await _gameNightService.GetUpcomingGameNights(3);

        var recent = statistics.RecentActivities.FirstOrDefault();
        var lastSession = recent == null
            ? null
            : new HomepageLastSessionResponse(
                recent.Id,
                recent.GameTitle,
                recent.LocationName,
                recent.Start);

        var upcomingMeetups = upcomingGameNights
            .Select(gameNight =>
            {
                var location = gameNight.Location?.Name;
                return new HomepageMeetupResponse(
                    gameNight.Id,
                    gameNight.Title,
                    location,
                    gameNight.StartDate,
                    $"{gameNight.StartDate:dd.MM.yyyy} · {location ?? "—"}");
            })
            .ToList();

        return Ok(new HomepageStatsResponse(
            statistics.TotalGames,
            statistics.ActivePlayers,
            statistics.SessionsPlayed,
            statistics.TotalCollectionValue,
            lastSession,
            upcomingMeetups));
    }

    private bool HasValidToken()
    {
        var expected = _configuration[TokenConfigurationKey];

        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var authorization = Request.Headers["Authorization"].ToString();
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

    private sealed record HomepageStatsResponse(
        int Games,
        int Players,
        int Sessions,
        double? CollectionValue,
        HomepageLastSessionResponse? LastSession,
        IReadOnlyList<HomepageMeetupResponse> UpcomingMeetups);

    private sealed record HomepageLastSessionResponse(
        int Id,
        string Game,
        string? Location,
        DateTime Start);

    private sealed record HomepageMeetupResponse(
        int Id,
        string Title,
        string? Location,
        DateTime Start,
        string Display);
}
