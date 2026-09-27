namespace BoardGameTracker.Common.Extensions;

public static class BggPlayExtensions
{
    public static bool HasPlaceholderDate(this DateTime date) =>
        date.Date == DateTime.MinValue.Date
        || date.Date == new DateTime(1900, 1, 1);
}
