using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NovaWallet.Application.Abstractions;
using NovaWallet.Infrastructure.Migrations;
using NovaWallet.Infrastructure.Persistence;

namespace NovaWallet.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        // Resolved lazily so tests can override the connection string via configuration.
        services.AddSingleton(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>()
                .GetConnectionString(DatabaseOptions.ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{DatabaseOptions.ConnectionStringName}' is not configured.");

            return new NpgsqlDataSourceBuilder(connectionString).Build();
        });

        services.AddSingleton<IWalletStore, WalletStore>();
        services.AddSingleton<ILedgerReader, LedgerReader>();
        services.AddSingleton<ILedgerUnitOfWorkFactory, LedgerUnitOfWorkFactory>();
        services.AddSingleton<DatabaseMigrator>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgres", tags: [ReadyTag]);

        return services;
    }
}
