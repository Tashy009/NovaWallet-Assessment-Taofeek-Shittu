using NovaWallet.Application.Common;

namespace NovaWallet.Application.Credits;

public interface ICreditService
{
    Task<Result<CreditOutcome>> CreditAsync(
        Caller caller, CreditWalletCommand command, string correlationId, CancellationToken cancellationToken);
}

public sealed record CreditWalletCommand(Guid WalletId, long AmountKobo, string? ExternalReference, string? Narration);

public sealed record CreditReceipt(
    Guid TransactionId,
    Guid WalletId,
    string Currency,
    long AmountKobo,
    long BalanceAfterKobo,
    string ExternalReference,
    string? Narration,
    DateTimeOffset CreatedAt);

public sealed record CreditOutcome(CreditReceipt Receipt, bool Replayed);
