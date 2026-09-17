using System.Text.Json;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;

namespace NovaWallet.Application.Transfers;

public static class TransferOutcomeSerializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(TransferReceipt receipt) =>
        JsonSerializer.Serialize(new StoredOutcome(receipt, null), Json);

    public static string Serialize(AppError rejection) =>
        JsonSerializer.Serialize(
            new StoredOutcome(null, new StoredRejection(rejection.Kind, rejection.Code, rejection.Message, rejection.Details)), Json);

    public static TransferOutcome DeserializeReplay(string storedBody)
    {
        var stored = JsonSerializer.Deserialize<StoredOutcome>(storedBody, Json)
            ?? throw new LedgerInvariantViolationException("Stored idempotency outcome is empty.");

        if (stored.Receipt is not null)
        {
            return new TransferOutcome(stored.Receipt, null, Replayed: true);
        }

        var r = stored.Rejection ?? throw new LedgerInvariantViolationException("Stored idempotency outcome has no result.");
        return new TransferOutcome(null, new AppError(r.Kind, r.Code, r.Message) { Details = r.Details }, Replayed: true);
    }

    private sealed record StoredOutcome(TransferReceipt? Receipt, StoredRejection? Rejection);

    private sealed record StoredRejection(ErrorKind Kind, string Code, string Message, IReadOnlyDictionary<string, object?>? Details);
}
