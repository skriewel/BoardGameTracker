using BoardGamer.BoardGameGeek.BoardGameGeekXmlApi2;
using BoardGameTracker.Common.Entities;
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
    public async Task ImportPlays_ShouldSkipSessionsForGamesNotInLocalCollection()
    {
        _bggClient
            .Setup(x => x.GetPlaysAsync(It.IsAny<PlaysRequest>()))
            .ReturnsAsync(CreateResponse(
                1,
                1,
                [
                    new PlaysResponse.Play
                    {
                        Id = 200,
                        Date = new DateTime(2024, 7, 1),
                        Quantity = 3,
                        Length = 60,
                        Item = new PlaysResponse.Item { ObjectId = 999 }
                    }
                ]));

        var result = await _service.ImportPlays("testuser");

        result.ImportedSessions.Should().Be(0);
        result.SkippedMissingGameSessions.Should().Be(3);
        _sessionRepository.Verify(x => x.CreateRangeAsync(It.IsAny<List<Session>>()), Times.Never);
        _unitOfWork.Verify(x => x.SaveChangesAsync(default), Times.Never);
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
