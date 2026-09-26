using BoardGamer.BoardGameGeek.BoardGameGeekXmlApi2;
using BoardGameTracker.Common.Entities;
using BoardGameTracker.Common.Helpers;
using BoardGameTracker.Core.Datastore.Interfaces;
using BoardGameTracker.Core.Games;
using BoardGameTracker.Core.Games.Interfaces;
using BoardGameTracker.Core.Sessions.Interfaces;
using BoardGameTracker.Core.Settings.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BoardGameTracker.Tests.Services;

public class BggPlayImportServiceTests
{
    private readonly Mock<IBoardGameGeekXmlApi2Client> _bggClient = new();
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly Mock<IGameRepository> _gameRepository = new();
    private readonly Mock<ISessionRepository> _sessionRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<BggPlayImportService>> _logger = new();
    private readonly BggPlayImportService _service;

    public BggPlayImportServiceTests()
    {
        _settingsService.Setup(x => x.IsBggEnabled()).ReturnsAsync(true);
        _gameRepository.Setup(x => x.GetAllAsync()).ReturnsAsync([]);
        _sessionRepository.Setup(x => x.GetAllAsync()).ReturnsAsync([]);

        _service = new BggPlayImportService(
            _bggClient.Object,
            _settingsService.Object,
            _gameRepository.Object,
            _sessionRepository.Object,
            _unitOfWork.Object,
            _logger.Object);
    }

    [Fact]
    public async Task ImportPlays_ShouldSplitQuantityIntoDistinctSessions()
    {
        var game = CreateGame(7, 42);
        _gameRepository.Setup(x => x.GetAllAsync()).ReturnsAsync([game]);

        var response = CreateResponse(
            total: 1,
            page: 1,
            [
                new PlaysResponse.Play
                {
                    Id = 100,
                    Date = new DateTime(2024, 6, 15),
                    Quantity = 2,
                    Length = 45,
                    Comments = "BGG comment",
                    Item = new PlaysResponse.Item { ObjectId = 42, Name = "Test Game", ObjectType = "thing" }
                }
            ]);
        _bggClient.Setup(x => x.GetPlaysAsync(It.IsAny<PlaysRequest>())).ReturnsAsync(response);

        List<Session>? imported = null;
        _sessionRepository
            .Setup(x => x.CreateRangeAsync(It.IsAny<List<Session>>()))
            .Callback<List<Session>>(sessions => imported = sessions)
            .Returns(Task.CompletedTask);
        _unitOfWork.Setup(x => x.SaveChangesAsync(default)).ReturnsAsync(1);

        var result = await _service.ImportPlays("testuser");

        result.ImportedSessions.Should().Be(2);
        result.SkippedExistingSessions.Should().Be(0);
        result.SkippedMissingGameSessions.Should().Be(0);
        result.PagesFetched.Should().Be(1);
        result.MissingGamesReportFile.Should().BeNull();

        imported.Should().NotBeNull();
        imported!.Should().HaveCount(2);
        imported.Select(x => x.BggPlayId).Should().OnlyContain(x => x == 100);
        imported.Select(x => x.BggPlayIndex).Should().BeEquivalentTo([1, 2]);
        imported.Select(x => x.GameId).Should().OnlyContain(x => x == 7);
        imported.Select(x => x.Comment).Should().OnlyContain(x => x == "BGG comment");
        imported.Select(x => x.GetDuration()).Should().OnlyContain(x => x == TimeSpan.FromMinutes(45));
        imported.Select(x => x.Start).Should().OnlyContain(x =>
            x == new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        _sessionRepository.Verify(x => x.CreateRangeAsync(It.IsAny<List<Session>>()), Times.Once);
        _unitOfWork.Verify(x => x.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task ImportPlays_ShouldSkipAlreadyImportedQuantityEntries()
    {
        var game = CreateGame(7, 42);
        _gameRepository.Setup(x => x.GetAllAsync()).ReturnsAsync([game]);

        var existing = new Session(
            7,
            new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 6, 15, 0, 30, 0, DateTimeKind.Utc),
            string.Empty);
        existing.SetBggImportKey(100, 1);
        _sessionRepository.Setup(x => x.GetAllAsync()).ReturnsAsync([existing]);

        _bggClient
            .Setup(x => x.GetPlaysAsync(It.IsAny<PlaysRequest>()))
            .ReturnsAsync(CreateResponse(
                1,
                1,
                [
                    new PlaysResponse.Play
                    {
                        Id = 100,
                        Date = new DateTime(2024, 6, 15),
                        Quantity = 2,
                        Length = 30,
                        Item = new PlaysResponse.Item { ObjectId = 42 }
                    }
                ]));

        List<Session>? imported = null;
        _sessionRepository
            .Setup(x => x.CreateRangeAsync(It.IsAny<List<Session>>()))
            .Callback<List<Session>>(sessions => imported = sessions)
            .Returns(Task.CompletedTask);
        _unitOfWork.Setup(x => x.SaveChangesAsync(default)).ReturnsAsync(1);

        var result = await _service.ImportPlays("testuser");

        result.ImportedSessions.Should().Be(1);
        result.SkippedExistingSessions.Should().Be(1);
        imported.Should().ContainSingle();
        imported![0].BggPlayId.Should().Be(100);
        imported[0].BggPlayIndex.Should().Be(2);
    }

    [Fact]
    public async Task ImportPlays_ShouldWriteAggregatedReportForGamesNotInLocalCollection()
    {
        _bggClient
            .Setup(x => x.GetPlaysAsync(It.IsAny<PlaysRequest>()))
            .ReturnsAsync(CreateResponse(
                2,
                1,
                [
                    new PlaysResponse.Play
                    {
                        Id = 200,
                        Date = new DateTime(2024, 7, 1),
                        Quantity = 3,
                        Length = 60,
                        Item = new PlaysResponse.Item { ObjectId = 999, Name = "Missing Game" }
                    },
                    new PlaysResponse.Play
                    {
                        Id = 201,
                        Date = new DateTime(2024, 6, 30),
                        Quantity = 2,
                        Length = 45,
                        Item = new PlaysResponse.Item { ObjectId = 999, Name = "Missing Game" }
                    }
                ]));

        string? reportPath = null;
        try
        {
            var result = await _service.ImportPlays("testuser");

            result.ImportedSessions.Should().Be(0);
            result.SkippedMissingGameSessions.Should().Be(5);
            result.MissingGamesReportFile.Should().NotBeNullOrWhiteSpace();

            reportPath = Path.Combine(PathHelper.FullLogsPath, result.MissingGamesReportFile!);
            File.Exists(reportPath).Should().BeTrue();

            var lines = await File.ReadAllLinesAsync(reportPath);
            lines.Should().HaveCount(2);
            lines[0].Should().Be("BggId,Title,MissingSessions,FirstPlayDate,LastPlayDate");
            lines[1].Should().Be("999,\"Missing Game\",5,2024-06-30,2024-07-01");

            _sessionRepository.Verify(x => x.CreateRangeAsync(It.IsAny<List<Session>>()), Times.Never);
            _unitOfWork.Verify(x => x.SaveChangesAsync(default), Times.Never);
        }
        finally
        {
            if (reportPath != null && File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
    }

    [Fact]
    public async Task ImportPlays_ShouldSkipExpansionPlaysAndExcludeThemFromMissingReport()
    {
        _bggClient
            .Setup(x => x.GetPlaysAsync(It.IsAny<PlaysRequest>()))
            .ReturnsAsync(CreateResponse(
                2,
                1,
                [
                    new PlaysResponse.Play
                    {
                        Id = 300,
                        Date = new DateTime(2024, 8, 1),
                        Quantity = 2,
                        Length = 30,
                        Item = new PlaysResponse.Item
                        {
                            ObjectId = 1234,
                            Name = "Some Expansion",
                            SubTypes = ["boardgame", "boardgameexpansion"]
                        }
                    },
                    new PlaysResponse.Play
                    {
                        Id = 301,
                        Date = new DateTime(2024, 8, 2),
                        Quantity = 1,
                        Length = 60,
                        Item = new PlaysResponse.Item
                        {
                            ObjectId = 999,
                            Name = "Missing Base Game",
                            SubTypes = ["boardgame"]
                        }
                    }
                ]));

        string? reportPath = null;
        try
        {
            var result = await _service.ImportPlays("testuser");

            result.ImportedSessions.Should().Be(0);
            result.SkippedExpansionSessions.Should().Be(2);
            result.SkippedMissingGameSessions.Should().Be(1);
            result.MissingGamesReportFile.Should().NotBeNullOrWhiteSpace();

            reportPath = Path.Combine(PathHelper.FullLogsPath, result.MissingGamesReportFile!);
            var lines = await File.ReadAllLinesAsync(reportPath);

            lines.Should().HaveCount(2);
            lines[1].Should().Contain("999");
            lines[1].Should().Contain("Missing Base Game");
            lines.Should().NotContain(line => line.Contains("Some Expansion", StringComparison.Ordinal));
        }
        finally
        {
            if (reportPath != null && File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
    }

    [Fact]
    public async Task ImportPlays_ShouldFetchAllPages()
    {
        var game = CreateGame(7, 42);
        _gameRepository.Setup(x => x.GetAllAsync()).ReturnsAsync([game]);

        var firstPage = Enumerable.Range(1, 100)
            .Select(id => new PlaysResponse.Play
            {
                Id = id,
                Date = new DateTime(2024, 1, 1).AddDays(id - 1),
                Quantity = 1,
                Item = new PlaysResponse.Item { ObjectId = 42 }
            })
            .ToList();
        var secondPage = new List<PlaysResponse.Play>
        {
            new()
            {
                Id = 101,
                Date = new DateTime(2024, 4, 10),
                Quantity = 1,
                Item = new PlaysResponse.Item { ObjectId = 42 }
            }
        };

        _bggClient
            .Setup(x => x.GetPlaysAsync(It.IsAny<PlaysRequest>()))
            .ReturnsAsync((PlaysRequest request) =>
                request.Page == 1
                    ? CreateResponse(101, 1, firstPage)
                    : CreateResponse(101, 2, secondPage));

        _sessionRepository
            .Setup(x => x.CreateRangeAsync(It.IsAny<List<Session>>()))
            .Returns(Task.CompletedTask);
        _unitOfWork.Setup(x => x.SaveChangesAsync(default)).ReturnsAsync(1);

        var result = await _service.ImportPlays("testuser");

        result.ImportedSessions.Should().Be(101);
        result.PagesFetched.Should().Be(2);
        _bggClient.Verify(x => x.GetPlaysAsync(It.IsAny<PlaysRequest>()), Times.Exactly(2));
    }

    private static Game CreateGame(int id, int bggId)
    {
        var game = new Game("Test Game") { Id = id };
        game.UpdateBggId(bggId);
        return game;
    }

    private static PlaysResponse CreateResponse(
        int total,
        int page,
        IEnumerable<PlaysResponse.Play> plays)
    {
        return new PlaysResponse(new PlaysResponse.PlaysCollection
        {
            Username = "testuser",
            Total = total,
            Page = page,
            Plays = plays.ToList()
        });
    }
}
