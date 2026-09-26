using BoardGameTracker.Common.Models.Bgg;

namespace BoardGameTracker.Core.Games.Interfaces;

public interface IBggPlayImportService
{
    Task<BggPlayImportResult> ImportPlays(string userName);
}
