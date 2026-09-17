using NovaWallet.Application.Common;
using NovaWallet.Domain;

namespace NovaWallet.Application.Transfers;

public static class TransferCommandValidator
{
    public static AppError? Validate(TransferCommand command, string? idempotencyKey)
    {
        var errors = new ValidationErrors();
        errors.IdempotencyKey("Idempotency-Key", idempotencyKey);

        if (command.SourceWalletId == Guid.Empty)
        {
            errors.Add("sourceWalletId", "Value is required.");
        }

        if (command.DestinationWalletId == Guid.Empty)
        {
            errors.Add("destinationWalletId", "Value is required.");
        }
        else if (command.DestinationWalletId == command.SourceWalletId)
        {
            errors.Add("destinationWalletId", "Source and destination wallets must be different.");
        }

        errors.AmountKobo("amountKobo", command.AmountKobo);

        if (command.Currency is not null && command.Currency != Currency.Ngn)
        {
            errors.Add("currency", "Only NGN is supported.");
        }

        errors.Narration("narration", command.Narration);

        return errors.HasErrors ? errors.ToError() : null;
    }
}
