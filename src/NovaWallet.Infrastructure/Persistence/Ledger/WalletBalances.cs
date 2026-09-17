using Dapper;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence.Ledger;

internal sealed class WalletBalances(DbSession session) : IWalletBalances
{
    public Task<IReadOnlyList<LockedWallet>> LockPairForUpdateAsync(Guid first, Guid second, CancellationToken cancellationToken) =>
        DbSession.Guarded(async () =>
        {
            // ORDER BY id makes every transaction lock the pair in the same database-defined order: no A↔B deadlock.
            var rows = await session.Connection.QueryAsync<LockedWallet>(session.Command(
                """
                SELECT id AS Id, customer_id AS CustomerId, balance_kobo AS BalanceKobo,
                       daily_outbound_limit_kobo AS DailyOutboundLimitKobo, status AS Status
                FROM wallets
                WHERE id IN (@first, @second)
                ORDER BY id
                FOR UPDATE
                """,
                new { first, second },
                cancellationToken));

            return (IReadOnlyList<LockedWallet>)rows.AsList();
        });

    public Task<long?> TryDebitAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken) =>
        DbSession.Guarded(() => session.Connection.QuerySingleOrDefaultAsync<long?>(session.Command(
            """
            UPDATE wallets
            SET balance_kobo = balance_kobo - @amountKobo, updated_at = now()
            WHERE id = @walletId AND status = 'ACTIVE' AND balance_kobo >= @amountKobo
            RETURNING balance_kobo
            """,
            new { walletId, amountKobo },
            cancellationToken)));

    // Increment happens inside one statement under the row lock, so concurrent credits cannot lose updates.
    public Task<long?> TryCreditAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken) =>
        DbSession.Guarded(() => session.Connection.QuerySingleOrDefaultAsync<long?>(session.Command(
            """
            UPDATE wallets
            SET balance_kobo = balance_kobo + @amountKobo, updated_at = now()
            WHERE id = @walletId AND status = 'ACTIVE'
            RETURNING balance_kobo
            """,
            new { walletId, amountKobo },
            cancellationToken)));
}
