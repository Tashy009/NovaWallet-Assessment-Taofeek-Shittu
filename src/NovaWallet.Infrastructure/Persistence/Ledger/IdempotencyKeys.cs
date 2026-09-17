using Dapper;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence.Ledger;

internal sealed class IdempotencyKeys(DbSession session) : IIdempotencyKeys
{
    public Task<StoredIdempotencyResult?> TryClaimAsync(
        string customerId, string operation, string idempotencyKey, string requestHash, CancellationToken cancellationToken) =>
        DbSession.Guarded(async () =>
        {
            var parameters = new { customerId, operation, idempotencyKey, requestHash };

            // Blocks on a concurrent uncommitted claim of the same key, then returns no row if that claim committed.
            var claimed = await session.Connection.QuerySingleOrDefaultAsync<int?>(session.Command(
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

            var previous = await session.Connection.QuerySingleOrDefaultAsync<StoredIdempotencyResult>(session.Command(
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

    public Task CompleteAsync(
        string customerId, string operation, string idempotencyKey, int responseStatus, string responseBody,
        Guid? transactionId, CancellationToken cancellationToken) =>
        DbSession.Guarded(async () =>
        {
            var updated = await session.Connection.ExecuteAsync(session.Command(
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
}
