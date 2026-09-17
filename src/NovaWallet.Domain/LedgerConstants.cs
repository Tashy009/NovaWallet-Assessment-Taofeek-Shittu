namespace NovaWallet.Domain;

public static class LedgerLimits
{
    // ₦10bn per transaction keeps any single amount far from bigint overflow and inside JSON-safe integers.
    public const long MaxTransactionAmountKobo = 1_000_000_000_000;

    public const long DefaultDailyOutboundLimitKobo = 50_000_000;
}

public static class TransactionTypes
{
    public const string Credit = "CREDIT";
    public const string Transfer = "TRANSFER";
}

public static class EntryDirections
{
    public const string Debit = "DEBIT";
    public const string Credit = "CREDIT";
}

public static class AuditActions
{
    public const string WalletDebited = "WALLET_DEBITED";
    public const string WalletCredited = "WALLET_CREDITED";
}

public static class WalletStatuses
{
    public const string Active = "ACTIVE";
    public const string Frozen = "FROZEN";
}
