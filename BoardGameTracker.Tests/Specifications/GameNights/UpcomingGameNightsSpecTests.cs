using System;
using System.Collections.Generic;
using System.Linq;
using Ardalis.Specification;
using BoardGameTracker.Common.Entities;
using BoardGameTracker.Core.GameNights.Specifications;
using FluentAssertions;
using Xunit;

namespace BoardGameTracker.Tests.Specifications.GameNights;

public class UpcomingGameNightsSpecTests
{
    [Fact]
    public void Evaluate_ShouldReturnNextFutureGameNightsInAscendingOrder()
    {
        var now = new DateTime(2030, 1, 1);
        var past = GameNight.Create("Past", "", now.AddDays(-1), 1, 1);
        var first = GameNight.Create("First", "", now.AddDays(1), 1, 1);
        var second = GameNight.Create("Second", "", now.AddDays(2), 1, 1);
        var third = GameNight.Create("Third", "", now.AddDays(3), 1, 1);
        var nights = new List<GameNight> { third, past, second, first };
        var spec = new UpcomingGameNightsSpec(now, 2);

        var result = spec.Evaluate(nights).ToList();

        result.Select(x => x.Title).Should().ContainInOrder("First", "Second");
        result.Should().HaveCount(2);
        spec.AsNoTracking.Should().BeTrue();
    }
}
