namespace NovaWallet.Domain;

// Daily limits reset at midnight Africa/Lagos (WAT).
public static class BusinessDay
{
    // Fallback only: WAT is a fixed UTC+1 with no DST.
    private static readonly TimeSpan WatOffset = TimeSpan.FromHours(1);

    private static readonly TimeZoneInfo Lagos = ResolveLagos();

    public static DateOnly For(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, Lagos);
        return DateOnly.FromDateTime(local.DateTime);
    }

    private static TimeZoneInfo ResolveLagos()
    {
        foreach (var id in new[] { "Africa/Lagos", "W. Central Africa Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // try the next id
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("WAT", WatOffset, "West Africa Time", "WAT");
    }
}
