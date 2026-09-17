using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Outbox;

internal sealed class OutboxProcessor(
    NpgsqlDataSource dataSource,
    IEventPublisher publisher,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger) : IOutboxProcessor
{
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // SKIP LOCKED lets several relay instances drain the outbox in parallel without claiming the same row.
        var batch = (await connection.QueryAsync<OutboxEnvelope>(new CommandDefinition(
            """
            SELECT id AS EventId, event_type AS EventType, aggregate_id AS AggregateId,
                   payload::text AS Payload, occurred_at AS OccurredAt, attempts AS Attempts
            FROM outbox_messages
            WHERE published_at IS NULL AND next_attempt_at <= now()
            ORDER BY occurred_at, id
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """,
            new { batchSize = settings.BatchSize },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        foreach (var envelope in batch)
        {
            try
            {
                await publisher.PublishAsync(envelope, cancellationToken);

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE outbox_messages
                    SET published_at = now(), attempts = attempts + 1, last_error = NULL
                    WHERE id = @id
                    """,
                    new { id = envelope.EventId },
                    transaction,
                    cancellationToken: cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Outbox publish failed for {EventType} {EventId} (attempt {Attempt}): {ErrorType}",
                    envelope.EventType, envelope.EventId, envelope.Attempts + 1, ex.GetType().Name);

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE outbox_messages
                    SET attempts = attempts + 1,
                        last_error = left(@error, 500),
                        next_attempt_at = now() + make_interval(secs => least(power(2, attempts + 1), @maxBackoffSeconds))
                    WHERE id = @id
                    """,
                    new { id = envelope.EventId, error = $"{ex.GetType().Name}: {ex.Message}", maxBackoffSeconds = settings.MaxBackoffSeconds },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }
}
