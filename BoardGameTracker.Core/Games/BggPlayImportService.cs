using System.Globalization;
using System.Net;
using System.Text;
using BoardGamer.BoardGameGeek.BoardGameGeekXmlApi2;
using BoardGameTracker.Common.DTOs.Commands;
using BoardGameTracker.Common.Entities;
using BoardGameTracker.Common.Exceptions;
using BoardGameTracker.Common.Extensions;
using BoardGameTracker.Common.Helpers;
using BoardGameTracker.Common.Models.Bgg;
using BoardGameTracker.Core.Datastore.Interfaces;
using BoardGameTracker.Core.Games.Interfaces;
using BoardGameTracker.Core.Locations.Interfaces;
using BoardGameTracker.Core.Players.Interfaces;
using BoardGameTracker.Core.Sessions.Interfaces;
using BoardGameTracker.Core.Settings.Interfaces;
using Microsoft.Extensions.Logging;

namespace BoardGameTracker.Core.Games;

public class BggPlayImportService : IBggPlayImportService
{
    private const int PageSize = 100;

    private readonly IBoardGameGeekXmlApi2Client _bggClient;
    private readonly ISettingsService _settingsService;
    private readonly IGameRepository _gameRepository;
    private readonly ISessionRepository _sessionRepository;
    private readonly IPlayerService _playerService;
    private readonly ILocationService _locationService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<BggPlayImportService> _logger;

    public BggPlayImportService(
        IBoardGameGeekXmlApi2Client bggClient,
        ISettingsService settingsService,
        IGameRepository gameRepository,
        ISessionRepository sessionRepository,
        IPlayerService playerService,
        ILocationService locationService,
        IUnitOfWork unitOfWork,
        ILogger<BggPlayImportService> logger)
    {
        _bggClient = bggClient;
        _settingsService = settingsService;
        _gameRepository = gameRepository;
        _sessionRepository = sessionRepository;
        _playerService = playerService;
        _locationService = locationService;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<BggPlayImportResult> ImportPlays(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("BGG username is required.", nameof(userName));
        }

        await EnsureBggConfiguredAsync();

        var gamesByBggId = (await _gameRepository.GetAllAsync())
            .Where(game => game.BggId.HasValue)
            .GroupBy(game => game.BggId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        var existingImportedSessions = await _sessionRepository.GetBggImportedSessionsForUpdate();
        var existingSessionsByKey = existingImportedSessions
            .Where(session => session.BggPlayId.HasValue && session.BggPlayIndex.HasValue)
            .ToDictionary(
                session => (session.BggPlayId!.Value, session.BggPlayIndex!.Value),
                session => session);

        var eligiblePlays = new List<EligiblePlay>();
        var page = 1;
        var totalBggPlayEntries = 0;
        var skippedMissingGameSessions = 0;
        var skippedExpansionSessions = 0;
        var skippedInvalidSessions = 0;
        var pagesFetched = 0;
        var missingGames = new Dictionary<int, MissingGameInfo>();

        while (true)
        {
            PlaysResponse response;
            try
            {
                response = await _bggClient.GetPlaysAsync(
                    new PlaysRequest(userName.Trim(), subType: "boardgame", page: page));
            }
            catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(ex, "BGG API key is invalid or expired");
                throw new ValidationException("Invalid BGG API key. Please check your API key in settings.");
            }
            catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(ex, "BGG rate-limited the plays request for user {UserName}", userName);
                throw new BggRateLimitException();
            }

            var result = response.Result;
            if (!response.Succeeded || result?.Plays == null || result.Plays.Count == 0)
            {
                break;
            }

            pagesFetched++;
            totalBggPlayEntries = Math.Max(totalBggPlayEntries, result.Total);

            foreach (var play in result.Plays)
            {
                var quantity = Math.Max(1, play.Quantity);

                if (play.Id <= 0 || play.Item == null || play.Item.ObjectId <= 0 || play.Date.HasPlaceholderDate())
                {
                    skippedInvalidSessions += quantity;
                    continue;
                }

                if (play.Item.SubTypes?.Any(subType =>
                        string.Equals(subType, "boardgameexpansion", StringComparison.OrdinalIgnoreCase)) == true)
                {
                    skippedExpansionSessions += quantity;
                    continue;
                }

                if (!gamesByBggId.TryGetValue(play.Item.ObjectId, out var game))
                {
                    skippedMissingGameSessions += quantity;

                    if (!missingGames.TryGetValue(play.Item.ObjectId, out var missingGame))
                    {
                        missingGame = new MissingGameInfo(
                            play.Item.ObjectId,
                            string.IsNullOrWhiteSpace(play.Item.Name) ? $"BGG {play.Item.ObjectId}" : play.Item.Name.Trim(),
                            0,
                            play.Date.Date,
                            play.Date.Date);
                    }

                    missingGames[play.Item.ObjectId] = missingGame with
                    {
                        MissingSessions = missingGame.MissingSessions + quantity,
                        FirstPlayDate = play.Date.Date < missingGame.FirstPlayDate ? play.Date.Date : missingGame.FirstPlayDate,
                        LastPlayDate = play.Date.Date > missingGame.LastPlayDate ? play.Date.Date : missingGame.LastPlayDate
                    };

                    continue;
                }

                eligiblePlays.Add(new EligiblePlay(play, game.Id, quantity));
            }

            if (result.Plays.Count < PageSize || (result.Total > 0 && page * PageSize >= result.Total))
            {
                break;
            }

            page++;
        }

        var playersByName = (await _playerService.GetList())
            .Where(player => !string.IsNullOrWhiteSpace(player.Name))
            .GroupBy(player => player.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(player => player.Id).First(),
                StringComparer.OrdinalIgnoreCase);

        var playerNames = eligiblePlays
            .SelectMany(entry => entry.Play.Players?.AsEnumerable() ?? Enumerable.Empty<PlaysResponse.Player>())
            .Select(GetBggPlayerName)
            .Where(name => name != null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var createdPlayers = 0;
        foreach (var playerName in playerNames)
        {
            if (playersByName.ContainsKey(playerName))
            {
                continue;
            }

            var player = await _playerService.Create(new CreatePlayerCommand { Name = playerName });
            playersByName[playerName] = player;
            createdPlayers++;
        }

        var locationsByName = (await _locationService.GetLocations())
            .Where(location => !string.IsNullOrWhiteSpace(location.Name))
            .GroupBy(location => location.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(location => location.Id).First(),
                StringComparer.OrdinalIgnoreCase);

        var locationNames = eligiblePlays
            .Where(entry => Enumerable.Range(1, entry.Quantity).Any(playIndex =>
                !existingSessionsByKey.TryGetValue((entry.Play.Id, playIndex), out var existingSession)
                || existingSession.LocationId == null))
            .Select(entry => NormalizeName(entry.Play.Location))
            .Where(name => name != null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var createdLocations = 0;
        foreach (var locationName in locationNames)
        {
            if (locationsByName.ContainsKey(locationName))
            {
                continue;
            }

            var location = await _locationService.Create(new CreateLocationCommand { Name = locationName });
            locationsByName[locationName] = location;
            createdLocations++;
        }

        var sessionsToImport = new List<Session>();
        var importedSessions = 0;
        var updatedExistingSessions = 0;
        var skippedExistingSessions = 0;

        foreach (var entry in eligiblePlays)
        {
            var play = entry.Play;
            var start = DateTime.SpecifyKind(play.Date.Date, DateTimeKind.Utc);
            var minutes = Math.Max(0, play.Length);

            for (var playIndex = 1; playIndex <= entry.Quantity; playIndex++)
            {
                var key = (play.Id, playIndex);
                var isExisting = existingSessionsByKey.TryGetValue(key, out var session);

                if (!isExisting)
                {
                    session = new Session(
                        entry.GameId,
                        start,
                        start.AddMinutes(minutes),
                        play.Comments ?? string.Empty);
                    session.SetBggImportKey(play.Id, playIndex);

                    sessionsToImport.Add(session);
                    importedSessions++;
                }
                else
                {
                    skippedExistingSessions++;
                }

                var metadataChanged = ApplyBggMetadata(
                    session!,
                    play,
                    playersByName,
                    locationsByName);

                if (isExisting && metadataChanged)
                {
                    updatedExistingSessions++;
                }
            }
        }

        if (sessionsToImport.Count > 0)
        {
            await _sessionRepository.CreateRangeAsync(sessionsToImport);
        }

        if (sessionsToImport.Count > 0 || updatedExistingSessions > 0)
        {
            await _unitOfWork.SaveChangesAsync();
        }

        var missingGamesReportFile = missingGames.Count > 0
            ? await WriteMissingGamesReportAsync(userName.Trim(), missingGames.Values)
            : null;

        _logger.LogInformation(
            "Imported {ImportedSessions} BGG sessions for {UserName}; updated {UpdatedExisting} existing sessions; created {CreatedPlayers} players and {CreatedLocations} locations; skipped {Existing} existing, {MissingGame} without local game, {Expansion} expansion, {Invalid} invalid",
            importedSessions,
            userName,
            updatedExistingSessions,
            createdPlayers,
            createdLocations,
            skippedExistingSessions,
            skippedMissingGameSessions,
            skippedExpansionSessions,
            skippedInvalidSessions);

        return new BggPlayImportResult(
            totalBggPlayEntries,
            importedSessions,
            updatedExistingSessions,
            createdPlayers,
            createdLocations,
            skippedExistingSessions,
            skippedMissingGameSessions,
            skippedExpansionSessions,
            skippedInvalidSessions,
            pagesFetched,
            missingGamesReportFile);
    }

    private static bool ApplyBggMetadata(
        Session session,
        PlaysResponse.Play play,
        IReadOnlyDictionary<string, Player> playersByName,
        IReadOnlyDictionary<string, Location> locationsByName)
    {
        var changed = false;

        var locationName = NormalizeName(play.Location);
        if (session.LocationId == null
            && locationName != null
            && locationsByName.TryGetValue(locationName, out var location))
        {
            session.SetLocationId(location.Id);
            changed = true;
        }

        foreach (var bggPlayer in play.Players?.AsEnumerable() ?? Enumerable.Empty<PlaysResponse.Player>())
        {
            var playerName = GetBggPlayerName(bggPlayer);
            if (playerName == null || !playersByName.TryGetValue(playerName, out var player))
            {
                continue;
            }

            var score = ParseBggScore(bggPlayer.Score);
            var playerSession = session.PlayerSessions.FirstOrDefault(
                existing => existing.PlayerId == player.Id);

            if (playerSession == null)
            {
                session.AddPlayerSession(player.Id, score, bggPlayer.New, bggPlayer.Win);
                changed = true;
                continue;
            }

            if (score.HasValue && playerSession.Score != score)
            {
                playerSession.UpdateScore(score);
                changed = true;
            }

            if (playerSession.FirstPlay != bggPlayer.New)
            {
                playerSession.UpdateFirstPlay(bggPlayer.New);
                changed = true;
            }

            if (playerSession.Won != bggPlayer.Win)
            {
                if (bggPlayer.Win)
                {
                    playerSession.MarkAsWinner();
                }
                else
                {
                    playerSession.MarkAsLoser();
                }

                changed = true;
            }
        }

        return changed;
    }

    private static string? GetBggPlayerName(PlaysResponse.Player player) =>
        NormalizeName(player.Name) ?? NormalizeName(player.Username);

    private static string? NormalizeName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? ParseBggScore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (double.TryParse(
                normalized,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var score))
        {
            return score;
        }

        if (normalized.Contains(',') && !normalized.Contains('.'))
        {
            normalized = normalized.Replace(',', '.');
            if (double.TryParse(
                    normalized,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out score))
            {
                return score;
            }
        }

        return null;
    }

    private async Task<string> WriteMissingGamesReportAsync(
        string userName,
        IEnumerable<MissingGameInfo> missingGames)
    {
        Directory.CreateDirectory(PathHelper.FullLogsPath);

        var safeUserName = new string(userName
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray());

        var fileName = $"bgg-missing-games-{safeUserName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        var filePath = Path.Combine(PathHelper.FullLogsPath, fileName);

        var lines = new List<string>
        {
            "BggId,Title,MissingSessions,FirstPlayDate,LastPlayDate"
        };

        lines.AddRange(missingGames
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.BggId)
            .Select(game => string.Join(",",
                game.BggId,
                CsvEscape(game.Title),
                game.MissingSessions,
                game.FirstPlayDate.ToString("yyyy-MM-dd"),
                game.LastPlayDate.ToString("yyyy-MM-dd"))));

        await File.WriteAllLinesAsync(filePath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _logger.LogInformation("Wrote missing BGG games report to {FilePath}", filePath);

        return fileName;
    }

    private static string CsvEscape(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record EligiblePlay(
        PlaysResponse.Play Play,
        int GameId,
        int Quantity);

    private sealed record MissingGameInfo(
        int BggId,
        string Title,
        int MissingSessions,
        DateTime FirstPlayDate,
        DateTime LastPlayDate);

    private async Task EnsureBggConfiguredAsync()
    {
        if (!await _settingsService.IsBggEnabled())
        {
            throw new BggFeatureDisabledException();
        }
    }
}
