namespace BoardGameTracker.Common.Models.Bgg;

public sealed record BggPrivateCollectionImportResult(
    int TotalRows,
    int StandaloneRows,
    int MatchedGames,
    int PurchaseDatesUpdated,
    int PricesUpdated,
    int ExistingPricesPreserved,
    int CadPricesConverted,
    int UsdPricesConverted,
    int GbpPricesConverted,
    int InvalidRows,
    int UnsupportedCurrencyRows,
    IReadOnlyList<int> MissingBggIds);
