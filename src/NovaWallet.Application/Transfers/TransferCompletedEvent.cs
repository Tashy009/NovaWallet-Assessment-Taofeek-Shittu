using System.Text.Json;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Application.Transfers;

// Integration event contract. Deliberately excludes balances and narration (data minimization).
public sealed record TransferCompletedEvent(
    Guid EventId,
    Guid TransactionId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    string Currency,
    DateOnly BusinessDate,
    DateTimeOffset OccurredAt,
    string CorrelationId)
{
    public const string EventType = "TransferCompleted";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public OutboxMessage ToOutboxMessage() =>
        new(EventId, EventType, TransactionId, JsonSerializer.Serialize(this, Json));
}
