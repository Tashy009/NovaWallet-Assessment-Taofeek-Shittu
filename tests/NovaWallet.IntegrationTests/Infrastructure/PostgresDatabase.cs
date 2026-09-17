using Npgsql;
using Testcontainers.PostgreSql;

namespace NovaWallet.IntegrationTests.Infrastructure;

// Real PostgreSQL via Testcontainers, or a throwaway database on the server in NOVAWALLET_TEST_POSTGRES.
public sealed class PostgresDatabase : IAsyncDisposable
{
    public const string AdminConnectionEnvironmentVariable = "NOVAWALLET_TEST_POSTGRES";

    // Below PostgreSQL's default max_connections (100) so concurrency tests queue instead of failing.
    private const int MaxPoolSize = 50;

    private readonly PostgreSqlContainer? _container;
    private readonly string? _adminConnectionString;
    private readonly string? _databaseName;

    private PostgresDatabase(string connectionString, PostgreSqlContainer? container, string? adminConnectionString, string? databaseName)
    {
        ConnectionString = connectionString;
        _container = container;
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
    }

    public string ConnectionString { get; }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public static async Task<PostgresDatabase> CreateAsync()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
            await container.StartAsync();
            return new PostgresDatabase(WithPoolSize(container.GetConnectionString(), database: null), container, null, null);
        }

        var databaseName = $"novawallet_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            // databaseName is generated above (hex only), never user input.
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new PostgresDatabase(WithPoolSize(adminConnectionString, databaseName), null, adminConnectionString, databaseName);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string WithPoolSize(string connectionString, string? database)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = MaxPoolSize };
        if (database is not null)
        {
            builder.Database = database;
        }

        return builder.ConnectionString;
    }
}
