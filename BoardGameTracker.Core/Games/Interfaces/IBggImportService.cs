using BoardGameTracker.Common.Entities;
using BoardGameTracker.Common.Models;
using BoardGameTracker.Common.Models.Bgg;

namespace BoardGameTracker.Core.Games.Interfaces;

public interface IBggImportService
{
    Task<Game?> ImportGameFromBgg(BggSearch search);
    Task<IList<BggImportGame>> ImportBggCollection(string userName);
    Task<BggPrivateCollectionImportResult> ImportPrivateCollectionCsv(Stream csvStream);
    Task ImportList(IList<ImportGame> games);
}
