namespace NovaWallet.Domain;

public enum TransferDecision
{
    Allowed,
    InsufficientFunds,
    DailyLimitExceeded,
}

public static class TransferPolicy
{
    // Inputs must be read while the source wallet row is locked.
    public static TransferDecision Evaluate(long balanceKobo, long spentTodayKobo, long amountKobo, long dailyLimitKobo)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountKobo);
        ArgumentOutOfRangeException.ThrowIfNegative(balanceKobo);
        ArgumentOutOfRangeException.ThrowIfNegative(spentTodayKobo);

        if (amountKobo > balanceKobo)
        {
            return TransferDecision.InsufficientFunds;
        }

        return checked(spentTodayKobo + amountKobo) > dailyLimitKobo
            ? TransferDecision.DailyLimitExceeded
            : TransferDecision.Allowed;
    }
}
