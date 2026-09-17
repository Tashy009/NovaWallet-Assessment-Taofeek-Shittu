using NovaWallet.Application.Common;

namespace NovaWallet.Application.Transfers;

public interface ITransferService
{
    Task<Result<TransferOutcome>> TransferAsync(
        Caller caller, TransferCommand command, string? idempotencyKey, string correlationId, CancellationToken cancellationToken);
}

public sealed record TransferCommand(
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    string? Currency,
    string? Narration);

public sealed record TransferReceipt(
    Guid TransactionId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    string Currency,
    long AmountKobo,
    long SourceBalanceAfterKobo,
    string? Narration,
    DateOnly BusinessDate,
    DateTimeOffset CreatedAt);

// Exactly one of Receipt or Rejection is set. Rejections are business outcomes stored against the idempotency key.
public sealed record TransferOutcome(TransferReceipt? Receipt, AppError? Rejection, bool Replayed);
