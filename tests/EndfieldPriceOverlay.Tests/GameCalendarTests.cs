using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Tests;

public sealed class GameCalendarTests
{
    [Theory]
    [InlineData(0, 0, 3)]
    [InlineData(3, 59, 3)]
    [InlineData(4, 0, 4)]
    [InlineData(23, 59, 4)]
    public void DailyResetAtFourAmDeterminesGameDate(int hour, int minute, int expectedDay)
    {
        var localTime = new DateTime(2026, 7, 4, hour, minute, 0);

        Assert.Equal(new DateOnly(2026, 7, expectedDay), GameCalendar.DateAt(localTime));
    }
}
