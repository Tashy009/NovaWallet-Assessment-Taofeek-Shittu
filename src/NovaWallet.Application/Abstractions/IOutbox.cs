namespace NovaWallet.Application.Abstractions;

public sealed record OutboxMessage(Guid Id, string EventType, Guid AggregateId, string Payload);

public sealed record OutboxEnvelope(Guid EventId, string EventType, Guid AggregateId, string Payload, DateTime OccurredAt, int Attempts);

// Write side: part of the ledger unit of work, so an event exists only if the ledger change committed.
public interface IOutbox
{
    Task AddAsync(OutboxMessage message, CancellationToken cancellationToken);
}

// Relay side: delivers committed events at least once; consumers must dedupe on EventId.
public interface IOutboxProcessor
{
    // Publishes one batch of due messages and returns how many were attempted.
    Task<int> PublishPendingAsync(CancellationToken cancellationToken);
}

public interface IEventPublisher
{
    Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken);
}
