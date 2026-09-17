using Microsoft.Extensions.Logging;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Outbox;

// Stand-in for a message broker (no broker is in scope); swap for a Kafka/Service Bus publisher without touching the relay.
internal sealed class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        logger.LogInformation("Published {EventType} {EventId} for aggregate {AggregateId}",
            envelope.EventType, envelope.EventId, envelope.AggregateId);
        return Task.CompletedTask;
    }
}
