using DbUp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NovaWallet.Infrastructure.Persistence;

namespace NovaWallet.Infrastructure.Migrations;

public sealed class DatabaseMigrator(
    IConfiguration configuration,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrator> logger)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{DatabaseOptions.ConnectionStringName}' is not configured.");

        await WaitForDatabaseAsync(connectionString, cancellationToken);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(DatabaseMigrator).Assembly,
                name => name.Contains(".Migrations.Scripts.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();

        var pending = upgrader.GetScriptsToExecute().Select(s => s.Name).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date");
            return;
        }

        logger.LogInformation("Applying {Count} migration script(s): {Scripts}", pending.Count, pending);

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            logger.LogCritical(result.Error, "Migration failed on script {Script}", result.ErrorScript?.Name);
            throw new InvalidOperationException("Database migration failed.", result.Error);
        }

        logger.LogInformation("Database migration completed");
    }

    private async Task WaitForDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, options.Value.MigrationConnectAttempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException ex) when (attempt < attempts)
            {
                // Never log the connection string: it contains the password.
                logger.LogWarning("Database not reachable (attempt {Attempt}/{Attempts}): {Reason}",
                    attempt, attempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
