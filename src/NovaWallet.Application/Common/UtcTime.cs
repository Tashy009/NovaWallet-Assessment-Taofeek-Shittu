namespace NovaWallet.Application.Common;

public static class UtcTime
{
    // Npgsql returns timestamptz as DateTime; the database stores UTC.
    public static DateTimeOffset From(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
