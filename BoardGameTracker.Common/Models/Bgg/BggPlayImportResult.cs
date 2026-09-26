namespace BoardGameTracker.Common.Models.Bgg;

public sealed record BggPlayImportResult(
    int TotalBggPlayEntries,
    int ImportedSessions,
    int SkippedExistingSessions,
    int SkippedMissingGameSessions,
    int SkippedInvalidSessions,
    int PagesFetched,
    string? MissingGamesReportFile);
