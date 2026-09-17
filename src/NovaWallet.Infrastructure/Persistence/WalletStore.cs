using Dapper;
using Npgsql;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence;

internal sealed class WalletStore(NpgsqlDataSource dataSource) : IWalletStore
{
    private const string Columns = """
        id AS Id,
        customer_id AS CustomerId,
        currency AS Currency,
        balance_kobo AS BalanceKobo,
        daily_outbound_limit_kobo AS DailyOutboundLimitKobo,
        status AS Status,
        created_at AS CreatedAt
        """;

    public async Task<WalletSnapshot?> TryCreateAsync(
        Guid walletId, string customerId, long dailyOutboundLimitKobo, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<WalletSnapshot>(new CommandDefinition(
            $"""
            INSERT INTO wallets (id, customer_id, daily_outbound_limit_kobo)
            VALUES (@walletId, @customerId, @dailyOutboundLimitKobo)
            ON CONFLICT ON CONSTRAINT uq_wallets_customer_currency DO NOTHING
            RETURNING {Columns}
            """,
            new { walletId, customerId, dailyOutboundLimitKobo },
            cancellationToken: cancellationToken));
    }

    public async Task<WalletSnapshot?> FindAsync(Guid walletId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<WalletSnapshot>(new CommandDefinition(
            $"SELECT {Columns} FROM wallets WHERE id = @walletId",
            new { walletId },
            cancellationToken: cancellationToken));
    }

    public async Task<WalletSnapshot?> FindByCustomerAsync(string customerId, string currency, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<WalletSnapshot>(new CommandDefinition(
            $"SELECT {Columns} FROM wallets WHERE customer_id = @customerId AND currency = @currency",
            new { customerId, currency },
            cancellationToken: cancellationToken));
    }
}
