using System.Globalization;
using System.Net;
using System.Text;
using BoardGamer.BoardGameGeek.BoardGameGeekXmlApi2;
using BoardGameTracker.Common.Entities;
using BoardGameTracker.Common.Exceptions;
using BoardGameTracker.Common.Extensions;
using BoardGameTracker.Common.Models;
using BoardGameTracker.Common.Models.Bgg;
using BoardGameTracker.Core.Datastore.Interfaces;
using BoardGameTracker.Core.Games.Factories;
using BoardGameTracker.Core.Games.Interfaces;
using BoardGameTracker.Core.Settings.Interfaces;
using Microsoft.Extensions.Logging;

namespace BoardGameTracker.Core.Games;

public class BggImportService : IBggImportService
{
    private readonly IBoardGameGeekXmlApi2Client _bggClient;
    private readonly ISettingsService _settingsService;
    private readonly IGameFactory _gameFactory;
    private readonly IGameRepository _gameRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<BggImportService> _logger;

    public BggImportService(
        IBoardGameGeekXmlApi2Client bggClient,
        ISettingsService settingsService,
        IGameFactory gameFactory,
        IGameRepository gameRepository,
        IUnitOfWork unitOfWork,
        ILogger<BggImportService> logger)
    {
        _bggClient = bggClient;
        _settingsService = settingsService;
        _gameFactory = gameFactory;
        _gameRepository = gameRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Game?> ImportGameFromBgg(BggSearch search)
    {
        await EnsureBggConfiguredAsync();

        var existingGame = await _gameRepository.GetGameByBggId(search.BggId);
        if (existingGame != null)
        {
            return existingGame;
        }

        _logger.LogDebug("Searching BGG for game with id {BggId}", search.BggId);
        var item = await FetchThingFromBgg(search.BggId);
        if (item == null)
        {
            return null;
        }

        var game = await _gameFactory.CreateFromBggAsync(
            item,
            search.HasScoring,
            search.State,
            search.Price.HasValue ? (decimal?)search.Price.Value : null,
            search.AdditionDate,
            search.ShopUrl);

        await _gameRepository.CreateAsync(game);
        await _unitOfWork.SaveChangesAsync();
        return game;
    }

    public async Task<IList<BggImportGame>> ImportBggCollection(string userName)
    {
        await EnsureBggConfiguredAsync();
        _logger.LogInformation("Starting BGG collection import for user {UserName}", userName);

        CollectionResponse response;
        try
        {
            var request = new CollectionRequest(userName, subType: "boardgame", excludeSubType: "boardgameexpansion");
            response = await _bggClient.GetCollectionAsync(request);
        }
        catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(ex, "BGG API key is invalid or expired");
            throw new ValidationException("Invalid BGG API key. Please check your API key in settings.");
        }
        catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning(ex, "BGG rate-limited the collection request for user {UserName}", userName);
            throw new BggRateLimitException();
        }
        catch (BoardGameGeekHttpException ex)
        {
            _logger.LogWarning(ex, "BGG API request failed for collection import of user {UserName}", userName);
            throw;
        }
        catch (Exception ex) when (ex.Message == "Retries exhausted")
        {
            _logger.LogWarning(ex, "BGG collection for user {UserName} was still being prepared after retries", userName);
            throw new BggCollectionPreparingException();
        }

        var games = (response.Result?.AsEnumerable() ?? Enumerable.Empty<CollectionResponse.Item>())
            .Where(x => x.HasSupportedGameState())
            .OrderByDescending(x => x.Status.HasSupportedGameState())
            .ThenByDescending(x => x.Status.LastModified)
            .DistinctBy(x => x.ObjectId)
            .Select(collectionItem => new BggImportGame
            {
                BggId = collectionItem.ObjectId,
                Title = collectionItem.Name,
                State = collectionItem.ToGameState(),
                ImageUrl = !string.IsNullOrWhiteSpace(collectionItem.Thumbnail)
                    ? collectionItem.Thumbnail
                    : collectionItem.Image ?? string.Empty,
                LastModified = collectionItem.Status.LastModified
            })
            .ToList();

        var knownBggIds = games.Select(game => game.BggId).ToHashSet();
        var playedGames = await FetchPlayedGamesNotInCollection(userName, knownBggIds);
        games.AddRange(playedGames);

        return games
            .OrderBy(game => game.Title)
            .ToList();
    }

    public async Task<BggPrivateCollectionImportResult> ImportPrivateCollectionCsv(Stream csvStream)
    {
        ArgumentNullException.ThrowIfNull(csvStream);

        const decimal cadToEur = 0.62m;
        const decimal usdToEur = 0.88m;
        const decimal gbpToEur = 1.16m;

        using var rows = ReadCsvRows(csvStream).GetEnumerator();
        if (!rows.MoveNext())
        {
            throw new ValidationException("The BGG collection CSV is empty.");
        }

        var headers = rows.Current;
        var columns = headers
            .Select((header, index) => (Header: header.Trim(), Index: index))
            .Where(column => !string.IsNullOrWhiteSpace(column.Header))
            .GroupBy(column => column.Header, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Index,
                StringComparer.OrdinalIgnoreCase);

        foreach (var requiredColumn in new[] { "objectid", "itemtype", "pricepaid", "pp_currency", "acquisitiondate" })
        {
            if (!columns.ContainsKey(requiredColumn))
            {
                throw new ValidationException($"The BGG collection CSV is missing the '{requiredColumn}' column.");
            }
        }

        var totalRows = 0;
        var standaloneRows = 0;
        var matchedGames = 0;
        var purchaseDatesUpdated = 0;
        var pricesUpdated = 0;
        var existingPricesPreserved = 0;
        var cadPricesConverted = 0;
        var usdPricesConverted = 0;
        var gbpPricesConverted = 0;
        var invalidRows = 0;
        var unsupportedCurrencyRows = 0;
        var missingBggIds = new HashSet<int>();

        while (rows.MoveNext())
        {
            var fields = rows.Current;
            if (fields.Length == 0)
            {
                continue;
            }

            totalRows++;

            string GetField(string name)
            {
                var columnIndex = columns[name];
                return columnIndex < fields.Length ? fields[columnIndex].Trim() : string.Empty;
            }

            if (!string.Equals(GetField("itemtype"), "standalone", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            standaloneRows++;

            if (!int.TryParse(GetField("objectid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bggId)
                || bggId <= 0)
            {
                invalidRows++;
                continue;
            }

            var game = await _gameRepository.GetGameByBggId(bggId);
            if (game == null)
            {
                missingBggIds.Add(bggId);
                continue;
            }

            matchedGames++;

            var acquisitionDateText = GetField("acquisitiondate");
            if (!string.IsNullOrWhiteSpace(acquisitionDateText))
            {
                if (DateTime.TryParseExact(
                        acquisitionDateText,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var acquisitionDate))
                {
                    game.UpdateAdditionDate(DateTime.SpecifyKind(acquisitionDate.Date, DateTimeKind.Utc));
                    purchaseDatesUpdated++;
                }
                else
                {
                    invalidRows++;
                }
            }

            var priceText = GetField("pricepaid");
            if (string.IsNullOrWhiteSpace(priceText))
            {
                continue;
            }

            if (!decimal.TryParse(
                    priceText,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var sourcePrice)
                || sourcePrice < 0)
            {
                invalidRows++;
                continue;
            }

            if (game.BuyingPrice is { Amount: > 0 })
            {
                existingPricesPreserved++;
                continue;
            }

            var currency = GetField("pp_currency").ToUpperInvariant();
            decimal eurPrice;
            switch (currency)
            {
                case "EUR":
                    eurPrice = sourcePrice;
                    break;
                case "CAD":
                    eurPrice = sourcePrice * cadToEur;
                    cadPricesConverted++;
                    break;
                case "USD":
                    eurPrice = sourcePrice * usdToEur;
                    usdPricesConverted++;
                    break;
                case "GBP":
                    eurPrice = sourcePrice * gbpToEur;
                    gbpPricesConverted++;
                    break;
                default:
                    unsupportedCurrencyRows++;
                    continue;
            }

            game.UpdateBuyingPrice(eurPrice);
            pricesUpdated++;
        }

        if (purchaseDatesUpdated > 0 || pricesUpdated > 0)
        {
            await _unitOfWork.SaveChangesAsync();
        }

        _logger.LogInformation(
            "Imported BGG private collection CSV: {MatchedGames} matched games, {DatesUpdated} purchase dates updated, {PricesUpdated} prices updated, {MissingGames} missing BGG ids",
            matchedGames,
            purchaseDatesUpdated,
            pricesUpdated,
            missingBggIds.Count);

        return new BggPrivateCollectionImportResult(
            totalRows,
            standaloneRows,
            matchedGames,
            purchaseDatesUpdated,
            pricesUpdated,
            existingPricesPreserved,
            cadPricesConverted,
            usdPricesConverted,
            gbpPricesConverted,
            invalidRows,
            unsupportedCurrencyRows,
            missingBggIds.OrderBy(id => id).ToList());
    }

    public async Task ImportList(IList<ImportGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        await EnsureBggConfiguredAsync();
        _logger.LogInformation("Importing {Count} games from BGG", games.Count);

        var toImport = new List<ImportGame>();
        foreach (var importGame in games)
        {
            var existingGame = await _gameRepository.GetGameByBggId(importGame.BggId);
            if (existingGame != null)
            {
                _logger.LogDebug("BGG game with id {BggId} already imported, skipping", importGame.BggId);
                continue;
            }

            toImport.Add(importGame);
        }

        var items = toImport.Count > 0
            ? await FetchThingsFromBgg(toImport.Select(x => x.BggId).ToList())
            : new Dictionary<int, ThingResponse.Item>();

        var imported = 0;
        foreach (var importGame in toImport)
        {
            try
            {
                if (!items.TryGetValue(importGame.BggId, out var item))
                {
                    _logger.LogWarning("BGG game with id {BggId} not found, skipping", importGame.BggId);
                    continue;
                }

                var game = await _gameFactory.CreateFromBggAsync(
                    item,
                    importGame.HasScoring,
                    importGame.State,
                    ToSafeDecimalPrice(importGame.Price),
                    importGame.AddedDate);

                await _gameRepository.CreateAsync(game);
                imported++;
            }
            catch (ValidationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import BGG game {BggId}, skipping", importGame.BggId);
            }
        }

        await _unitOfWork.SaveChangesAsync();
        _logger.LogInformation("BGG import completed, {Imported}/{Count} games imported", imported, games.Count);
    }

    private async Task<List<BggImportGame>> FetchPlayedGamesNotInCollection(
        string userName,
        HashSet<int> knownBggIds)
    {
        const int pageSize = 100;
        var page = 1;
        var playedGames = new Dictionary<int, BggImportGame>();

        while (true)
        {
            PlaysResponse response;
            try
            {
                response = await _bggClient.GetPlaysAsync(
                    new PlaysRequest(userName, subType: "boardgame", page: page));
            }
            catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(ex, "BGG API key is invalid or expired");
                throw new ValidationException("Invalid BGG API key. Please check your API key in settings.");
            }
            catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(ex, "BGG rate-limited the plays request while building collection import for {UserName}", userName);
                throw new BggRateLimitException();
            }
            catch (BoardGameGeekHttpException ex)
            {
                _logger.LogWarning(ex, "BGG plays request failed while building collection import for {UserName}", userName);
                throw;
            }

            var result = response.Result;
            if (!response.Succeeded || result?.Plays == null || result.Plays.Count == 0)
            {
                break;
            }

            foreach (var play in result.Plays)
            {
                if (play.Item == null
                    || play.Item.ObjectId <= 0
                    || play.Date.HasPlaceholderDate()
                    || play.Item.SubTypes?.Any(subType =>
                        string.Equals(subType, "boardgameexpansion", StringComparison.OrdinalIgnoreCase)) == true)
                {
                    continue;
                }

                var bggId = play.Item.ObjectId;
                if (knownBggIds.Contains(bggId))
                {
                    continue;
                }

                var playDate = DateTime.SpecifyKind(play.Date.Date, DateTimeKind.Utc);
                var title = string.IsNullOrWhiteSpace(play.Item.Name)
                    ? $"BGG {bggId}"
                    : play.Item.Name.Trim();

                if (!playedGames.TryGetValue(bggId, out var existing))
                {
                    playedGames[bggId] = new BggImportGame
                    {
                        BggId = bggId,
                        Title = title,
                        State = BoardGameTracker.Common.Enums.GameState.NotOwned,
                        ImageUrl = string.Empty,
                        LastModified = playDate
                    };
                    continue;
                }

                if (playDate > existing.LastModified)
                {
                    existing.LastModified = playDate;
                }

                if (existing.Title.StartsWith("BGG ", StringComparison.Ordinal) && !title.StartsWith("BGG ", StringComparison.Ordinal))
                {
                    existing.Title = title;
                }
            }

            if (result.Plays.Count < pageSize || (result.Total > 0 && page * pageSize >= result.Total))
            {
                break;
            }

            page++;
        }

        return playedGames.Values.ToList();
    }

    private static IEnumerable<string[]> ReadCsvRows(Stream csvStream)
    {
        using var reader = new StreamReader(
            csvStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        while (reader.Read() is var next && next >= 0)
        {
            var character = (char)next;
            hasContent = true;

            if (inQuotes)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    row.Add(field.ToString());
                    field.Clear();
                    yield return row.ToArray();
                    row.Clear();
                    hasContent = false;
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row.ToArray();
                    row.Clear();
                    hasContent = false;
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (inQuotes)
        {
            throw new ValidationException("The BGG collection CSV contains an unterminated quoted field.");
        }

        if (hasContent || row.Count > 0 || field.Length > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }

    private static decimal? ToSafeDecimalPrice(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        try
        {
            return (decimal)value;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private async Task<Dictionary<int, ThingResponse.Item>> FetchThingsFromBgg(IReadOnlyList<int> bggIds)
    {
        const int chunkSize = 20;
        var items = new Dictionary<int, ThingResponse.Item>();

        for (var offset = 0; offset < bggIds.Count; offset += chunkSize)
        {
            var chunk = bggIds.Skip(offset).Take(chunkSize).ToArray();
            try
            {
                var request = new ThingRequest(chunk, stats: true);
                var response = await _bggClient.GetThingAsync(request);
                if (!response.Succeeded || response.Result == null)
                {
                    continue;
                }

                foreach (var item in response.Result)
                {
                    items[item.Id] = item;
                }
            }
            catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(ex, "BGG API key is invalid or expired");
                throw new ValidationException("Invalid BGG API key. Please check your API key in settings.");
            }
            catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(ex, "BGG rate-limited the request while importing games");
                throw new BggRateLimitException();
            }
            catch (BoardGameGeekHttpException ex)
            {
                _logger.LogWarning(ex, "BGG API request failed while importing a batch of games");
            }
        }

        return items;
    }

    private async Task<ThingResponse.Item?> FetchThingFromBgg(int bggId)
    {
        try
        {
            var request = new ThingRequest([bggId], stats: true);
            var response = await _bggClient.GetThingAsync(request);
            if (!response.Succeeded)
            {
                return null;
            }

            return response.Result?.FirstOrDefault();
        }
        catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(ex, "BGG API key is invalid or expired");
            throw new ValidationException("Invalid BGG API key. Please check your API key in settings.");
        }
        catch (BoardGameGeekHttpException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning(ex, "BGG rate-limited the request for game {BggId}", bggId);
            throw new BggRateLimitException();
        }
        catch (BoardGameGeekHttpException ex)
        {
            _logger.LogWarning(ex, "BGG API request failed for game {BggId}", bggId);
            return null;
        }
    }

    private async Task EnsureBggConfiguredAsync()
    {
        var enabled = await _settingsService.IsBggEnabled();
        if (!enabled)
        {
            throw new BggFeatureDisabledException();
        }
    }
}
