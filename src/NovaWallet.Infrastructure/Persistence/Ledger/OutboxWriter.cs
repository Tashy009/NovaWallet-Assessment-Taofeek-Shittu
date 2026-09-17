using Dapper;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence.Ledger;

internal sealed class OutboxWriter(DbSession session) : IOutbox
{
    public Task AddAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        DbSession.Guarded(() => session.Connection.ExecuteAsync(session.Command(
            """
            INSERT INTO outbox_messages (id, event_type, aggregate_id, payload)
            VALUES (@Id, @EventType, @AggregateId, CAST(@Payload AS jsonb))
            """,
            message,
            cancellationToken)));
}
