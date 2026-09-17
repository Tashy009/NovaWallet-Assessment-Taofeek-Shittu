using System.Data;
using Dapper;
using Npgsql;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence;

internal sealed class LedgerUnitOfWorkFactory(NpgsqlDataSource dataSource) : ILedgerUnitOfWorkFactory
{
    public async Task<ILedgerUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            return new LedgerUnitOfWork(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class LedgerUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction) : ILedgerUnitOfWork
{
    public Task<StoredIdempotencyResult?> ClaimIdempotencyKeyAsync(
        string customerId, string operation, string idempotencyKey, string requestHash, CancellationToken cancellationToken) =>
        Guarded(async () =>
        {
            var parameters = new { customerId, operation, idempotencyKey, requestHash };

            // Blocks on a concurrent uncommitted claim of the same key, then returns no row if that claim committed.
            var claimed = await connection.QuerySingleOrDefaultAsync<int?>(Command(
                """
                INSERT INTO idempotency_keys (customer_id, operation, idempotency_key, request_hash)
                VALUES (@customerId, @operation, @idempotencyKey, @requestHash)
                ON CONFLICT (customer_id, operation, idempotency_key) DO NOTHING
                RETURNING 1
                """,
                parameters,
                cancellationToken));

            if (claimed is not null)
            {
                return null;
            }

            var previous = await connection.QuerySingleOrDefaultAsync<StoredIdempotencyResult>(Command(
                """
                SELECT request_hash AS RequestHash, response_status AS ResponseStatus, response_body::text AS ResponseBody
                FROM idempotency_keys
                WHERE customer_id = @customerId AND operation = @operation AND idempotency_key = @idempotencyKey
                """,
                parameters,
                cancellationToken));

            return previous is { ResponseStatus: > 0 }
                ? previous
                : throw new LedgerInvariantViolationException("Idempotency key exists without a committed outcome.");
        });

    public Task CompleteIdempotencyKeyAsync(
        string customerId, string operation, string idempotencyKey, int responseStatus, string responseBody,
        Guid? transactionId, CancellationToken cancellationToken) =>
        Guarded(async () =>
        {
            var updated = await connection.ExecuteAsync(Command(
                """
                UPDATE idempotency_keys
                SET response_status = @responseStatus,
                    response_body = CAST(@responseBody AS jsonb),
                    transaction_id = @transactionId,
                    completed_at = now()
                WHERE customer_id = @customerId AND operation = @operation AND idempotency_key = @idempotencyKey
                  AND response_status = 0
                """,
                new { customerId, operation, idempotencyKey, responseStatus, responseBody, transactionId },
                cancellationToken));

            return updated == 1
                ? updated
                : throw new LedgerInvariantViolationException("Idempotency key was not claimed by this transaction.");
        });

    public Task<IReadOnlyList<LockedWallet>> LockWalletsAsync(Guid first, Guid second, CancellationToken cancellationToken) =>
        Guarded(async () =>
        {
            // ORDER BY id makes every transaction lock the pair in the same database-defined order: no A↔B deadlock.
            var rows = await connection.QueryAsync<LockedWallet>(Command(
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

    public Task<long> GetDailyOutboundTotalAsync(Guid walletId, DateOnly businessDate, CancellationToken cancellationToken) =>
        Guarded(() => connection.ExecuteScalarAsync<long>(Command(
            """
            SELECT COALESCE(
                (SELECT total_kobo FROM daily_outbound_totals WHERE wallet_id = @walletId AND business_date = @businessDate),
                0)
            """,
            new { walletId, businessDate },
            cancellationToken)));

    public Task<long?> DebitWalletAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken) =>
        Guarded(() => connection.QuerySingleOrDefaultAsync<long?>(Command(
            """
            UPDATE wallets
            SET balance_kobo = balance_kobo - @amountKobo, updated_at = now()
            WHERE id = @walletId AND status = 'ACTIVE' AND balance_kobo >= @amountKobo
            RETURNING balance_kobo
            """,
            new { walletId, amountKobo },
            cancellationToken)));

    // Increment happens inside one statement under the row lock, so concurrent credits cannot lose updates.
    public Task<long?> CreditWalletAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken) =>
        Guarded(() => connection.QuerySingleOrDefaultAsync<long?>(Command(
            """
            UPDATE wallets
            SET balance_kobo = balance_kobo + @amountKobo, updated_at = now()
            WHERE id = @walletId AND status = 'ACTIVE'
            RETURNING balance_kobo
            """,
            new { walletId, amountKobo },
            cancellationToken)));

    public Task<long?> AddDailyOutboundAsync(
        Guid walletId, DateOnly businessDate, long amountKobo, long limitKobo, CancellationToken cancellationToken) =>
        Guarded(() => connection.QuerySingleOrDefaultAsync<long?>(Command(
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

    public async Task<DateTime> InsertTransactionAsync(LedgerTransactionRecord record, CancellationToken cancellationToken)
    {
        try
        {
            return await Guarded(() => connection.ExecuteScalarAsync<DateTime>(Command(
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

    public Task InsertEntryAsync(LedgerEntryRecord entry, CancellationToken cancellationToken) =>
        Guarded(() => connection.ExecuteAsync(Command(
            """
            INSERT INTO ledger_entries (transaction_id, wallet_id, direction, amount_kobo, balance_after_kobo)
            VALUES (@TransactionId, @WalletId, @Direction, @AmountKobo, @BalanceAfterKobo)
            """,
            entry,
            cancellationToken)));

    public Task InsertAuditAsync(AuditRecord audit, CancellationToken cancellationToken) =>
        Guarded(() => connection.ExecuteAsync(Command(
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

    public Task CommitAsync(CancellationToken cancellationToken) =>
        Guarded(async () =>
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        });

    public async ValueTask DisposeAsync()
    {
        // Disposing an uncommitted NpgsqlTransaction rolls it back.
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private static async Task<T> Guarded<T>(Func<Task<T>> statement)
    {
        try
        {
            return await statement();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
        {
            throw new TransientConcurrencyException(ex);
        }
    }
}
