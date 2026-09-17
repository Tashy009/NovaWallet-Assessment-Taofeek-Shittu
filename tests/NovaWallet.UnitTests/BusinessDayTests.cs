using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class BusinessDayTests
{
    [Theory]
    // WAT = UTC+1, so the Lagos business day flips at 23:00:00 UTC.
    [InlineData("2026-08-24T22:59:59Z", "2026-08-24")]
    [InlineData("2026-08-24T23:00:00Z", "2026-08-25")]
    [InlineData("2026-08-24T00:30:00Z", "2026-08-24")]
    [InlineData("2026-12-31T23:00:00Z", "2027-01-01")]
    public void For_maps_utc_instant_to_lagos_calendar_date(string utcInstant, string expectedDate)
    {
        var instant = DateTimeOffset.Parse(utcInstant, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(DateOnly.Parse(expectedDate, System.Globalization.CultureInfo.InvariantCulture), BusinessDay.For(instant));
    }

    [Fact]
    public void For_is_independent_of_the_offset_the_instant_was_expressed_in()
    {
        var utc = new DateTimeOffset(2026, 8, 24, 23, 30, 0, TimeSpan.Zero);
        var sameInstantInNewYork = utc.ToOffset(TimeSpan.FromHours(-4));

        Assert.Equal(BusinessDay.For(utc), BusinessDay.For(sameInstantInNewYork));
    }
}
