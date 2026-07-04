namespace EndfieldPriceOverlay.Domain;

public static class GameCalendar
{
    public static readonly TimeSpan DailyResetTime = TimeSpan.FromHours(4);

    public static DateOnly DateAt(DateTime localTime)
    {
        var date = DateOnly.FromDateTime(localTime);
        return localTime.TimeOfDay < DailyResetTime ? date.AddDays(-1) : date;
    }
}
