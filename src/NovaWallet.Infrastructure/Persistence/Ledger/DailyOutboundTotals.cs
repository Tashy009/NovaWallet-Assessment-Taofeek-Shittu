using Dapper;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence.Ledger;

internal sealed class DailyOutboundTotals(DbSession session) : IDailyOutboundTotals
{
    public Task<long> GetAsync(Guid walletId, DateOnly businessDate, CancellationToken cancellationToken) =>
        DbSession.Guarded(() => session.Connection.ExecuteScalarAsync<long>(session.Command(
            """
            SELECT COALESCE(
                (SELECT total_kobo FROM daily_outbound_totals WHERE wallet_id = @walletId AND business_date = @businessDate),
                0)
            """,
            new { walletId, businessDate },
            cancellationToken)));

    public Task<long?> TryAddAsync(
        Guid walletId, DateOnly businessDate, long amountKobo, long limitKobo, CancellationToken cancellationToken) =>
        DbSession.Guarded(() => session.Connection.QuerySingleOrDefaultAsync<long?>(session.Command(
            """
            INSERT INTO daily_outbound_totals (wallet_id, business_date, total_kobo)
            SELECT @walletId, @businessDate, @amountKobo
            WHERE @amountKobo <= @limitKobo
            ON CONFLICT (wallet_id, business_date) DO UPDATE
            SET total_kobo = daily_outbound_totals.total_kobo + EXCLUDED.total_kobo, updated_at = now()
            WHERE daily_outbound_totals.total_kobo + EXCLUDED.total_kobo <= @limitKobo
            RETURNING total_kobo
            """,
            new { walletId, businessDate, amountKobo, limitKobo },
            cancellationToken)));
}
