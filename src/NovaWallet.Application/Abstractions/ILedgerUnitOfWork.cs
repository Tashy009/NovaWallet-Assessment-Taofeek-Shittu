namespace NovaWallet.Application.Abstractions;

public interface ILedgerUnitOfWorkFactory
{
    Task<ILedgerUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

// One READ COMMITTED database transaction shared by all parts; disposing without CommitAsync rolls everything back.
public interface ILedgerUnitOfWork : IAsyncDisposable
{
    IWalletBalances Wallets { get; }

    IIdempotencyKeys IdempotencyKeys { get; }

    IDailyOutboundTotals DailyOutboundTotals { get; }

    ILedgerJournal Journal { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IWalletBalances
{
    // SELECT ... ORDER BY id FOR UPDATE: every transfer locks its pair in the same order, so opposing transfers cannot deadlock.
    Task<IReadOnlyList<LockedWallet>> LockPairForUpdateAsync(Guid first, Guid second, CancellationToken cancellationToken);

    // Conditional debit; null if the wallet is not active or funds are insufficient.
    Task<long?> TryDebitAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken);

    // Atomic increment of an active wallet; null if no such active wallet.
    Task<long?> TryCreditAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken);
}

public interface IIdempotencyKeys
{
    // Null when this transaction claimed the key; otherwise the committed outcome of the earlier request.
    // A concurrent claim of the same key blocks until the other transaction commits or rolls back.
    Task<StoredIdempotencyResult?> TryClaimAsync(
        string customerId, string operation, string idempotencyKey, string requestHash, CancellationToken cancellationToken);

    Task CompleteAsync(
        string customerId, string operation, string idempotencyKey, int responseStatus, string responseBody,
        Guid? transactionId, CancellationToken cancellationToken);
}

public interface IDailyOutboundTotals
{
    Task<long> GetAsync(Guid walletId, DateOnly businessDate, CancellationToken cancellationToken);

    // Conditional upsert; null if the addition would exceed the limit.
    Task<long?> TryAddAsync(Guid walletId, DateOnly businessDate, long amountKobo, long limitKobo, CancellationToken cancellationToken);
}

public interface ILedgerJournal
{
    // Returns created_at; throws DuplicateExternalReferenceException if the external reference was already posted.
    Task<DateTime> AddTransactionAsync(LedgerTransactionRecord transaction, CancellationToken cancellationToken);

    Task AddEntryAsync(LedgerEntryRecord entry, CancellationToken cancellationToken);

    Task AddAuditAsync(AuditRecord audit, CancellationToken cancellationToken);
}
