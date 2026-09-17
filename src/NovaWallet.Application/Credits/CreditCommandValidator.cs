using NovaWallet.Application.Common;

namespace NovaWallet.Application.Credits;

public static class CreditCommandValidator
{
    public static AppError? Validate(CreditWalletCommand command)
    {
        var errors = new ValidationErrors();
        errors.AmountKobo("amountKobo", command.AmountKobo);
        errors.Reference("externalReference", command.ExternalReference, required: true);
        errors.Narration("narration", command.Narration);

        return errors.HasErrors ? errors.ToError() : null;
    }
}
