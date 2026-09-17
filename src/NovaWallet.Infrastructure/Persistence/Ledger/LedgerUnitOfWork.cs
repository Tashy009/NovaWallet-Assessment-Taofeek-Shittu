using System.Data;
using Npgsql;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence.Ledger;

internal sealed class LedgerUnitOfWorkFactory(NpgsqlDataSource dataSource) : ILedgerUnitOfWorkFactory
{
    public async Task<ILedgerUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            return new LedgerUnitOfWork(new DbSession(connection, transaction));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class LedgerUnitOfWork(DbSession session) : ILedgerUnitOfWork
{
    public IWalletBalances Wallets { get; } = new WalletBalances(session);

    public IIdempotencyKeys IdempotencyKeys { get; } = new IdempotencyKeys(session);

    public IDailyOutboundTotals DailyOutboundTotals { get; } = new DailyOutboundTotals(session);

    public ILedgerJournal Journal { get; } = new LedgerJournal(session);

    public Task CommitAsync(CancellationToken cancellationToken) =>
        DbSession.Guarded(async () =>
        {
            await session.Transaction.CommitAsync(cancellationToken);
            return true;
        });

    public async ValueTask DisposeAsync()
    {
        // Disposing an uncommitted NpgsqlTransaction rolls it back.
        await session.Transaction.DisposeAsync();
        await session.Connection.DisposeAsync();
    }
}
