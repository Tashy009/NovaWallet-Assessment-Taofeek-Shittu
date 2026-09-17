using Microsoft.Extensions.Logging.Abstractions;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;
using NovaWallet.Application.Transfers;

namespace NovaWallet.UnitTests;

public class TransferServiceTests
{
    private static readonly Caller Alice = new("cust-alice", new HashSet<string>());
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Guid Destination = Guid.NewGuid();

    [Fact]
    public async Task Invalid_command_is_rejected_before_any_database_transaction_starts()
    {
        var factory = new FakeUnitOfWorkFactory(new FakeUnitOfWork());
        var service = NewService(factory);

        var result = await service.TransferAsync(Alice, new TransferCommand(Source, Source, 0, "NGN", null), null, "corr", default);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal(0, factory.BeginCount);
    }

    [Fact]
    public async Task Reused_key_with_different_payload_never_locks_wallets_or_commits()
    {
        var uow = new FakeUnitOfWork
        {
            PreviousOutcome = new StoredIdempotencyResult("different-hash", 201, "{}"),
        };
        var service = NewService(new FakeUnitOfWorkFactory(uow));

        var result = await service.TransferAsync(Alice, new TransferCommand(Source, Destination, 1_000, "NGN", null), "key-1", "corr", default);

        Assert.Equal("idempotency_key_reused", result.Error!.Code);
        Assert.False(uow.WalletsLocked);
        Assert.False(uow.Committed);
    }

    [Fact]
    public async Task Guarded_debit_affecting_no_rows_aborts_without_commit()
    {
        var uow = new FakeUnitOfWork
        {
            LockedWallets =
            [
                new LockedWallet(Source, Alice.CustomerId, 10_000, 50_000_000, "ACTIVE"),
                new LockedWallet(Destination, "cust-bob", 0, 50_000_000, "ACTIVE"),
            ],
            DebitResult = null,
        };
        var service = NewService(new FakeUnitOfWorkFactory(uow));

        await Assert.ThrowsAsync<LedgerInvariantViolationException>(() =>
            service.TransferAsync(Alice, new TransferCommand(Source, Destination, 1_000, "NGN", null), "key-2", "corr", default));

        Assert.False(uow.Committed);
        Assert.True(uow.Disposed);
        Assert.Empty(uow.JournalWrites);
        Assert.Empty(uow.OutboxWrites);
    }

    [Fact]
    public async Task Insufficient_funds_stores_the_rejection_and_commits_without_touching_balances()
    {
        var uow = new FakeUnitOfWork
        {
            LockedWallets =
            [
                new LockedWallet(Source, Alice.CustomerId, 500, 50_000_000, "ACTIVE"),
                new LockedWallet(Destination, "cust-bob", 0, 50_000_000, "ACTIVE"),
            ],
        };
        var service = NewService(new FakeUnitOfWorkFactory(uow));

        var result = await service.TransferAsync(Alice, new TransferCommand(Source, Destination, 1_000, "NGN", null), "key-3", "corr", default);

        Assert.Equal("insufficient_funds", result.Value.Rejection!.Code);
        Assert.Equal(422, uow.CompletedStatus);
        Assert.True(uow.Committed);
        Assert.False(uow.BalanceMutated);
        Assert.Empty(uow.JournalWrites);
        Assert.Empty(uow.OutboxWrites);
    }

    [Fact]
    public async Task Successful_transfer_writes_exactly_one_transfer_completed_event_before_commit()
    {
        var uow = new FakeUnitOfWork
        {
            LockedWallets =
            [
                new LockedWallet(Source, Alice.CustomerId, 10_000, 50_000_000, "ACTIVE"),
                new LockedWallet(Destination, "cust-bob", 0, 50_000_000, "ACTIVE"),
            ],
            DebitResult = 9_000,
        };
        var service = NewService(new FakeUnitOfWorkFactory(uow));

        var result = await service.TransferAsync(Alice, new TransferCommand(Source, Destination, 1_000, "NGN", "rent"), "key-5", "corr-5", default);

        var message = Assert.Single(uow.OutboxWrites);
        Assert.Equal(TransferCompletedEvent.EventType, message.EventType);
        Assert.Equal(result.Value.Receipt!.TransactionId, message.AggregateId);
        Assert.True(uow.OutboxWrittenBeforeCommit);
        Assert.Contains("\"amountKobo\":1000", message.Payload);
        Assert.Contains("\"correlationId\":\"corr-5\"", message.Payload);
        Assert.DoesNotContain("rent", message.Payload);
        Assert.DoesNotContain("balance", message.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transient_database_conflict_retries_the_whole_transaction()
    {
        var factory = new FakeUnitOfWorkFactory(new FakeUnitOfWork()) { FailBeginWithTransient = true };
        var service = NewService(factory);

        await Assert.ThrowsAsync<TransientConcurrencyException>(() =>
            service.TransferAsync(Alice, new TransferCommand(Source, Destination, 1_000, "NGN", null), "key-4", "corr", default));

        Assert.Equal(ConcurrencyRetryPolicy.MaxAttempts, factory.BeginCount);
    }

    private static TransferService NewService(ILedgerUnitOfWorkFactory factory) =>
        new(factory, new ConcurrencyRetryPolicy(), TimeProvider.System, NullLogger<TransferService>.Instance);

    private sealed class FakeUnitOfWorkFactory(FakeUnitOfWork uow) : ILedgerUnitOfWorkFactory
    {
        public int BeginCount { get; private set; }

        public bool FailBeginWithTransient { get; init; }

        public Task<ILedgerUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            BeginCount++;
            return FailBeginWithTransient
                ? throw new TransientConcurrencyException(new InvalidOperationException("40001"))
                : Task.FromResult<ILedgerUnitOfWork>(uow);
        }
    }

    private sealed class FakeUnitOfWork : ILedgerUnitOfWork, IWalletBalances, IIdempotencyKeys, IDailyOutboundTotals, ILedgerJournal, IOutbox
    {
        public List<OutboxMessage> OutboxWrites { get; } = [];

        public bool OutboxWrittenBeforeCommit { get; private set; }

        public IOutbox Outbox => this;

        public Task AddAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            OutboxWrites.Add(message);
            OutboxWrittenBeforeCommit = !Committed;
            return Task.CompletedTask;
        }

        public StoredIdempotencyResult? PreviousOutcome { get; init; }

        public IReadOnlyList<LockedWallet> LockedWallets { get; init; } = [];

        public long? DebitResult { get; init; } = 0;

        public bool WalletsLocked { get; private set; }

        public bool BalanceMutated { get; private set; }

        public bool Committed { get; private set; }

        public bool Disposed { get; private set; }

        public int? CompletedStatus { get; private set; }

        public List<object> JournalWrites { get; } = [];

        public IWalletBalances Wallets => this;

        public IIdempotencyKeys IdempotencyKeys => this;

        public IDailyOutboundTotals DailyOutboundTotals => this;

        public ILedgerJournal Journal => this;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public Task<IReadOnlyList<LockedWallet>> LockPairForUpdateAsync(Guid first, Guid second, CancellationToken cancellationToken)
        {
            WalletsLocked = true;
            return Task.FromResult(LockedWallets);
        }

        public Task<long?> TryDebitAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken)
        {
            BalanceMutated = true;
            return Task.FromResult(DebitResult);
        }

        public Task<long?> TryCreditAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken)
        {
            BalanceMutated = true;
            return Task.FromResult<long?>(amountKobo);
        }

        public Task<StoredIdempotencyResult?> TryClaimAsync(
            string customerId, string operation, string idempotencyKey, string requestHash, CancellationToken cancellationToken) =>
            Task.FromResult(PreviousOutcome);

        public Task CompleteAsync(
            string customerId, string operation, string idempotencyKey, int responseStatus, string responseBody,
            Guid? transactionId, CancellationToken cancellationToken)
        {
            CompletedStatus = responseStatus;
            return Task.CompletedTask;
        }

        public Task<long> GetAsync(Guid walletId, DateOnly businessDate, CancellationToken cancellationToken) => Task.FromResult(0L);

        public Task<long?> TryAddAsync(Guid walletId, DateOnly businessDate, long amountKobo, long limitKobo, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(amountKobo);

        public Task<DateTime> AddTransactionAsync(LedgerTransactionRecord transaction, CancellationToken cancellationToken)
        {
            JournalWrites.Add(transaction);
            return Task.FromResult(DateTime.UtcNow);
        }

        public Task AddEntryAsync(LedgerEntryRecord entry, CancellationToken cancellationToken)
        {
            JournalWrites.Add(entry);
            return Task.CompletedTask;
        }

        public Task AddAuditAsync(AuditRecord audit, CancellationToken cancellationToken)
        {
            JournalWrites.Add(audit);
            return Task.CompletedTask;
        }
    }
}
