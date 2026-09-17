namespace NovaWallet.Application.Abstractions;

public sealed record WalletSnapshot(
    Guid Id,
    string CustomerId,
    string Currency,
    long BalanceKobo,
    long DailyOutboundLimitKobo,
    string Status,
    DateTime CreatedAt);

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

public interface IWalletStore
{
    // Returns null when the customer already has a wallet in that currency.
    Task<WalletSnapshot?> TryCreateAsync(Guid walletId, string customerId, long dailyOutboundLimitKobo, CancellationToken cancellationToken);

    Task<WalletSnapshot?> FindAsync(Guid walletId, CancellationToken cancellationToken);

    Task<WalletSnapshot?> FindByCustomerAsync(string customerId, string currency, CancellationToken cancellationToken);
}

public interface ILedgerReader
{
    Task<PostedCredit?> FindCreditByReferenceAsync(string externalReference, CancellationToken cancellationToken);
}

public sealed record LockedWallet(Guid Id, string CustomerId, long BalanceKobo, long DailyOutboundLimitKobo, string Status);

public sealed record StoredIdempotencyResult(string RequestHash, int ResponseStatus, string ResponseBody);

public interface ILedgerUnitOfWorkFactory
{
    Task<ILedgerUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

// One READ COMMITTED database transaction; disposing without CommitAsync rolls everything back.
public interface ILedgerUnitOfWork : IAsyncDisposable
{
    // Null when this transaction claimed the key; otherwise the committed result of the earlier request.
    // A concurrent claim of the same key blocks until the other transaction commits or rolls back.
    Task<StoredIdempotencyResult?> ClaimIdempotencyKeyAsync(
        string customerId, string operation, string idempotencyKey, string requestHash, CancellationToken cancellationToken);

    Task CompleteIdempotencyKeyAsync(
        string customerId, string operation, string idempotencyKey, int responseStatus, string responseBody,
        Guid? transactionId, CancellationToken cancellationToken);

    // Locks both wallets with SELECT ... ORDER BY id FOR UPDATE so every transfer acquires locks in the same order.
    Task<IReadOnlyList<LockedWallet>> LockWalletsAsync(Guid first, Guid second, CancellationToken cancellationToken);

    Task<long> GetDailyOutboundTotalAsync(Guid walletId, DateOnly businessDate, CancellationToken cancellationToken);

    // Conditional debit; null if the wallet is not active or funds are insufficient.
    Task<long?> DebitWalletAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken);

    // Conditional upsert; null if the addition would exceed the limit.
    Task<long?> AddDailyOutboundAsync(
        Guid walletId, DateOnly businessDate, long amountKobo, long limitKobo, CancellationToken cancellationToken);

    // Atomic increment of an ACTIVE wallet; returns the new balance, or null if no such active wallet.
    Task<long?> CreditWalletAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken);

    // Returns the row's created_at; throws DuplicateExternalReferenceException if the external reference was already posted.
    Task<DateTime> InsertTransactionAsync(LedgerTransactionRecord transaction, CancellationToken cancellationToken);

    Task InsertEntryAsync(LedgerEntryRecord entry, CancellationToken cancellationToken);

    Task InsertAuditAsync(AuditRecord audit, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}

public sealed class DuplicateExternalReferenceException(string externalReference)
    : Exception($"External reference '{externalReference}' has already been posted.");

// Deadlock or serialization failure: the transaction was rolled back and is safe to retry.
public sealed class TransientConcurrencyException(Exception inner)
    : Exception("The database aborted the transaction due to a concurrency conflict.", inner);

// A guarded statement affected no rows after the checks passed under lock; never expected, always rolled back.
public sealed class LedgerInvariantViolationException(string message) : Exception(message);
