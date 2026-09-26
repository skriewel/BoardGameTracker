using System.Net;
using System.Text;
using BoardGamer.BoardGameGeek.BoardGameGeekXmlApi2;
using BoardGameTracker.Common.Entities;
using BoardGameTracker.Common.Exceptions;
using BoardGameTracker.Common.Helpers;
using BoardGameTracker.Common.Models.Bgg;
using BoardGameTracker.Core.Datastore.Interfaces;
using BoardGameTracker.Core.Games.Interfaces;
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
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<BggPlayImportService> _logger;

    public BggPlayImportService(
        IBoardGameGeekXmlApi2Client bggClient,
        ISettingsService settingsService,
        IGameRepository gameRepository,
        ISessionRepository sessionRepository,
        IUnitOfWork unitOfWork,
        ILogger<BggPlayImportService> logger)
    {
        _bggClient = bggClient;
        _settingsService = settingsService;
        _gameRepository = gameRepository;
        _sessionRepository = sessionRepository;
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

        var importedKeys = (await _sessionRepository.GetAllAsync())
            .Where(session => session.BggPlayId.HasValue && session.BggPlayIndex.HasValue)
            .Select(session => (session.BggPlayId!.Value, session.BggPlayIndex!.Value))
            .ToHashSet();

        var sessionsToImport = new List<Session>();
        var page = 1;
        var totalBggPlayEntries = 0;
        var importedSessions = 0;
        var skippedExistingSessions = 0;
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

                if (play.Id <= 0 || play.Item == null || play.Item.ObjectId <= 0 || play.Date.Date == DateTime.MinValue.Date)
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

                var start = DateTime.SpecifyKind(play.Date.Date, DateTimeKind.Utc);
                var minutes = Math.Max(0, play.Length);

                for (var playIndex = 1; playIndex <= quantity; playIndex++)
                {
                    if (!importedKeys.Add((play.Id, playIndex)))
                    {
                        skippedExistingSessions++;
                        continue;
                    }

                    var session = new Session(
                        game.Id,
                        start,
                        start.AddMinutes(minutes),
                        play.Comments ?? string.Empty);
                    session.SetBggImportKey(play.Id, playIndex);

                    sessionsToImport.Add(session);
                    importedSessions++;
                }
            }

            if (result.Plays.Count < PageSize || (result.Total > 0 && page * PageSize >= result.Total))
            {
                break;
            }

            page++;
        }

        if (sessionsToImport.Count > 0)
        {
            await _sessionRepository.CreateRangeAsync(sessionsToImport);
            await _unitOfWork.SaveChangesAsync();
        }

        var missingGamesReportFile = missingGames.Count > 0
            ? await WriteMissingGamesReportAsync(userName.Trim(), missingGames.Values)
            : null;

        _logger.LogInformation(
            "Imported {ImportedSessions} BGG sessions for {UserName}; skipped {Existing} existing, {MissingGame} without local game, {Expansion} expansion, {Invalid} invalid",
            importedSessions,
            userName,
            skippedExistingSessions,
            skippedMissingGameSessions,
            skippedExpansionSessions,
            skippedInvalidSessions);

        return new BggPlayImportResult(
            totalBggPlayEntries,
            importedSessions,
            skippedExistingSessions,
            skippedMissingGameSessions,
            skippedExpansionSessions,
            skippedInvalidSessions,
            pagesFetched,
            missingGamesReportFile);
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
