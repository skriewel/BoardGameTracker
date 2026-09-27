using Ardalis.Specification;
using BoardGameTracker.Common.Entities;

namespace BoardGameTracker.Core.GameNights.Specifications;

public sealed class UpcomingGameNightsSpec : Specification<GameNight>
{
    public UpcomingGameNightsSpec(DateTime now, int count)
    {
        Query
            .Where(x => x.StartDate >= now)
            .Include(x => x.Location)
            .OrderBy(x => x.StartDate)
            .Take(count)
            .AsNoTracking();
    }
}
