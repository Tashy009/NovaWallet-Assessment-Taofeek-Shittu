using Dapper;
using Npgsql;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence;

internal sealed class StatementReader(NpgsqlDataSource dataSource) : IStatementReader
{
    public async Task<IReadOnlyList<StatementEntryRecord>> ReadAsync(
        Guid walletId, long beforeEntryNo, int take, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Keyset pagination on ix_ledger_entries_wallet_newest; stable while new entries arrive.
        var rows = await connection.QueryAsync<StatementEntryRecord>(new CommandDefinition(
            """
            SELECT e.entry_no           AS EntryNo,
                   e.transaction_id     AS TransactionId,
                   t.type               AS Type,
                   e.direction          AS Direction,
                   e.amount_kobo        AS AmountKobo,
                   e.balance_after_kobo AS BalanceAfterKobo,
                   CASE WHEN e.direction = 'DEBIT' THEN t.destination_wallet_id ELSE t.source_wallet_id END
                                        AS CounterpartyWalletId,
                   t.external_reference AS ExternalReference,
                   t.narration          AS Narration,
                   e.created_at         AS CreatedAt
            FROM ledger_entries e
            JOIN ledger_transactions t ON t.id = e.transaction_id
            WHERE e.wallet_id = @walletId AND e.entry_no < @beforeEntryNo
            ORDER BY e.entry_no DESC
            LIMIT @take
            """,
            new { walletId, beforeEntryNo, take },
            cancellationToken: cancellationToken));

        return rows.AsList();
    }
}
