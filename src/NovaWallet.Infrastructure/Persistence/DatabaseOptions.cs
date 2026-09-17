namespace NovaWallet.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "Ledger";

    public bool RunMigrationsOnStartup { get; init; } = true;

    public int MigrationConnectAttempts { get; init; } = 10;
}
