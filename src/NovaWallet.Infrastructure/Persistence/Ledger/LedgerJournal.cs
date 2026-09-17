using Dapper;
using Npgsql;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence.Ledger;

internal sealed class LedgerJournal(DbSession session) : ILedgerJournal
{
    public async Task<DateTime> AddTransactionAsync(LedgerTransactionRecord record, CancellationToken cancellationToken)
    {
        try
        {
            return await DbSession.Guarded(() => session.Connection.ExecuteScalarAsync<DateTime>(session.Command(
                """
                INSERT INTO ledger_transactions
                    (id, type, amount_kobo, source_wallet_id, destination_wallet_id, business_date,
                     external_reference, narration, initiated_by, correlation_id)
                VALUES
                    (@Id, @Type, @AmountKobo, @SourceWalletId, @DestinationWalletId, @BusinessDate,
                     @ExternalReference, @Narration, @InitiatedBy, @CorrelationId)
                RETURNING created_at
                """,
                record,
                cancellationToken)));
        }
        catch (PostgresException ex) when (ex is
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_ledger_transactions_external_reference",
        })
        {
            throw new DuplicateExternalReferenceException(record.ExternalReference!);
        }
    }

    public Task AddEntryAsync(LedgerEntryRecord entry, CancellationToken cancellationToken) =>
        DbSession.Guarded(() => session.Connection.ExecuteAsync(session.Command(
            """
            INSERT INTO ledger_entries (transaction_id, wallet_id, direction, amount_kobo, balance_after_kobo)
            VALUES (@TransactionId, @WalletId, @Direction, @AmountKobo, @BalanceAfterKobo)
            """,
            entry,
            cancellationToken)));

    public Task AddAuditAsync(AuditRecord audit, CancellationToken cancellationToken) =>
        DbSession.Guarded(() => session.Connection.ExecuteAsync(session.Command(
            """
            INSERT INTO audit_log
                (event_id, transaction_id, wallet_id, action, amount_kobo,
                 balance_before_kobo, balance_after_kobo, actor_id, correlation_id)
            VALUES
                (@EventId, @TransactionId, @WalletId, @Action, @AmountKobo,
                 @BalanceBeforeKobo, @BalanceAfterKobo, @ActorId, @CorrelationId)
            """,
            audit,
            cancellationToken)));
}
