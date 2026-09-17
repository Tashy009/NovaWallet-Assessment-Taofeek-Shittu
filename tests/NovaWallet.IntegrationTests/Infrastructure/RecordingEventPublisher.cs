using System.Collections.Concurrent;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.IntegrationTests.Infrastructure;

public sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly ConcurrentQueue<OutboxEnvelope> _published = new();

    public ConcurrentDictionary<Guid, bool> FailingAggregates { get; } = new();

    public IReadOnlyList<OutboxEnvelope> Published => _published.ToList();

    public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        if (FailingAggregates.ContainsKey(envelope.AggregateId))
        {
            throw new InvalidOperationException("Simulated broker outage.");
        }

        // A small delay widens the window in which concurrent relays could double-claim a row.
        await Task.Delay(1, cancellationToken);
        _published.Enqueue(envelope);
    }
}
