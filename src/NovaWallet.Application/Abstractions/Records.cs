namespace NovaWallet.Application.Abstractions;

public sealed record WalletSnapshot(
    Guid Id,
    string CustomerId,
    string Currency,
    long BalanceKobo,
    long DailyOutboundLimitKobo,
    string Status,
    DateTime CreatedAt);

public sealed record LockedWallet(Guid Id, string CustomerId, long BalanceKobo, long DailyOutboundLimitKobo, string Status);

public sealed record LedgerTransactionRecord(
    Guid Id,
    string Type,
    long AmountKobo,
    Guid? SourceWalletId,
    Guid DestinationWalletId,
    DateOnly BusinessDate,
    string? ExternalReference,
    string? Narration,
    string InitiatedBy,
    string CorrelationId);

public sealed record LedgerEntryRecord(
    Guid TransactionId,
    Guid WalletId,
    string Direction,
    long AmountKobo,
    long BalanceAfterKobo);

public sealed record AuditRecord(
    Guid EventId,
    Guid TransactionId,
    Guid WalletId,
    string Action,
    long AmountKobo,
    long BalanceBeforeKobo,
    long BalanceAfterKobo,
    string ActorId,
    string CorrelationId);

public sealed record PostedCredit(
    Guid TransactionId,
    Guid WalletId,
    long AmountKobo,
    long BalanceAfterKobo,
    string ExternalReference,
    string? Narration,
    DateTime CreatedAt);

public sealed record StoredIdempotencyResult(string RequestHash, int ResponseStatus, string ResponseBody);
