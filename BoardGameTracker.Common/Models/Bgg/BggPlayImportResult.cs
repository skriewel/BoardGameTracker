namespace BoardGameTracker.Common.Models.Bgg;

public sealed record BggPlayImportResult(
    int TotalBggPlayEntries,
    int ImportedSessions,
    int UpdatedExistingSessions,
    int CreatedPlayers,
    int CreatedLocations,
    int SkippedExistingSessions,
    int SkippedMissingGameSessions,
    int SkippedExpansionSessions,
    int SkippedInvalidSessions,
    int PagesFetched,
    string? MissingGamesReportFile);
