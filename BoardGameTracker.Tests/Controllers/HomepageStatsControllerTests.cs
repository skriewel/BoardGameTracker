using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BoardGameTracker.Api.Controllers;
using BoardGameTracker.Common.DTOs;
using BoardGameTracker.Common.Entities;
using BoardGameTracker.Core.Dashboard.Interfaces;
using BoardGameTracker.Core.GameNights.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace BoardGameTracker.Tests.Controllers;

public class HomepageStatsControllerTests
{
    [Fact]
    public async Task GetStats_ShouldRejectMissingOrWrongBearerToken()
    {
        var dashboardService = new Mock<IDashboardService>();
        var gameNightService = new Mock<IGameNightService>();
        var configuration = CreateConfiguration();

        var controller = CreateController(
            dashboardService.Object,
            gameNightService.Object,
            configuration);

        var missing = await controller.GetStats();
        missing.Should().BeOfType<UnauthorizedResult>();

        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer wrong";
        var wrong = await controller.GetStats();
        wrong.Should().BeOfType<UnauthorizedResult>();

        dashboardService.VerifyNoOtherCalls();
        gameNightService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetStats_ShouldReturnLastSessionAndUpcomingMeetups()
    {
        var dashboardService = new Mock<IDashboardService>();
        dashboardService.Setup(x => x.GetStatistics()).ReturnsAsync(new DashboardStatisticsDto
        {
            TotalGames = 25,
            ActivePlayers = 10,
            SessionsPlayed = 100,
            TotalCollectionValue = 887.5,
            RecentActivities =
            [
                new RecentActivityDto
                {
                    Id = 42,
                    GameTitle = "Heat: Pedal to the Metal",
                    LocationName = "Boardgame Cafe",
                    Start = new DateTime(2030, 9, 27, 18, 30, 0, DateTimeKind.Utc)
                }
            ]
        });

        var meetup = GameNight.Create(
            "Friday Games",
            "",
            new DateTime(2030, 10, 4, 19, 0, 0, DateTimeKind.Utc),
            1,
            2);
        meetup.Id = 7;

        var gameNightService = new Mock<IGameNightService>();
        gameNightService
            .Setup(x => x.GetUpcomingGameNights(3))
            .ReturnsAsync([meetup]);

        var controller = CreateController(
            dashboardService.Object,
            gameNightService.Object,
            CreateConfiguration());
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] =
            "Bearer homepage-secret";

        var result = await controller.GetStats();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(new
        {
            Games = 25,
            Players = 10,
            Sessions = 100,
            CollectionValue = (double?)887.5,
            LastSession = new
            {
                Id = 42,
                Game = "Heat: Pedal to the Metal",
                Location = "Boardgame Cafe",
                Start = new DateTime(2030, 9, 27, 18, 30, 0, DateTimeKind.Utc)
            },
            UpcomingMeetups = new[]
            {
                new
                {
                    Id = 7,
                    Title = "Friday Games",
                    Location = (string?)null,
                    Start = new DateTime(2030, 10, 4, 19, 0, 0, DateTimeKind.Utc),
                    Display = "04.10.2030 · —"
                }
            }
        });

        dashboardService.Verify(x => x.GetStatistics(), Times.Once);
        gameNightService.Verify(x => x.GetUpcomingGameNights(3), Times.Once);
        dashboardService.VerifyNoOtherCalls();
        gameNightService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetStats_ShouldReturnNullLastSessionAndEmptyMeetups_WhenNoneExist()
    {
        var dashboardService = new Mock<IDashboardService>();
        dashboardService
            .Setup(x => x.GetStatistics())
            .ReturnsAsync(new DashboardStatisticsDto { TotalGames = 5 });

        var gameNightService = new Mock<IGameNightService>();
        gameNightService
            .Setup(x => x.GetUpcomingGameNights(3))
            .ReturnsAsync([]);

        var controller = CreateController(
            dashboardService.Object,
            gameNightService.Object,
            CreateConfiguration());
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] =
            "Bearer homepage-secret";

        var result = await controller.GetStats();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(new
        {
            Games = 5,
            Players = 0,
            Sessions = 0,
            CollectionValue = (double?)null,
            LastSession = (object?)null,
            UpcomingMeetups = Array.Empty<object>()
        });
    }

    private static IConfiguration CreateConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HOMEPAGE_API_TOKEN"] = "homepage-secret"
            })
            .Build();
    }

    private static HomepageStatsController CreateController(
        IDashboardService dashboardService,
        IGameNightService gameNightService,
        IConfiguration configuration)
    {
        return new HomepageStatsController(dashboardService, gameNightService, configuration)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
