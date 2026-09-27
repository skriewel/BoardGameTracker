using System.Collections.Generic;
using System.Threading.Tasks;
using BoardGameTracker.Api.Controllers;
using BoardGameTracker.Common.DTOs;
using BoardGameTracker.Core.Dashboard.Interfaces;
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
        var service = new Mock<IDashboardService>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HOMEPAGE_API_TOKEN"] = "homepage-secret"
            })
            .Build();

        var controller = CreateController(service.Object, configuration);

        var missing = await controller.GetStats();
        missing.Should().BeOfType<UnauthorizedResult>();

        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer wrong";
        var wrong = await controller.GetStats();
        wrong.Should().BeOfType<UnauthorizedResult>();

        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetStats_ShouldReturnCompactDashboardStatistics()
    {
        var service = new Mock<IDashboardService>();
        service.Setup(x => x.GetStatistics()).ReturnsAsync(new DashboardStatisticsDto
        {
            TotalGames = 25,
            ActivePlayers = 10,
            SessionsPlayed = 100,
            TotalCollectionValue = 887.5
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HOMEPAGE_API_TOKEN"] = "homepage-secret"
            })
            .Build();

        var controller = CreateController(service.Object, configuration);
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] =
            "Bearer homepage-secret";

        var result = await controller.GetStats();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(new
        {
            Games = 25,
            Players = 10,
            Sessions = 100,
            CollectionValue = (double?)887.5
        });

        service.Verify(x => x.GetStatistics(), Times.Once);
        service.VerifyNoOtherCalls();
    }

    private static HomepageStatsController CreateController(
        IDashboardService service,
        IConfiguration configuration)
    {
        return new HomepageStatsController(service, configuration)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
