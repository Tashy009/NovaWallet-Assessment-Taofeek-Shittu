using Dapper;
using Npgsql;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence;

internal sealed class LedgerReader(NpgsqlDataSource dataSource) : ILedgerReader
{
    public async Task<PostedCredit?> FindCreditByReferenceAsync(string externalReference, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PostedCredit>(new CommandDefinition(
            """
            SELECT t.id                   AS TransactionId,
                   t.destination_wallet_id AS WalletId,
                   t.amount_kobo          AS AmountKobo,
                   e.balance_after_kobo   AS BalanceAfterKobo,
                   t.external_reference   AS ExternalReference,
                   t.narration            AS Narration,
                   t.created_at           AS CreatedAt
            FROM ledger_transactions t
            JOIN ledger_entries e ON e.transaction_id = t.id AND e.direction = 'CREDIT'
            WHERE t.type = 'CREDIT' AND t.external_reference = @externalReference
            """,
            new { externalReference },
            cancellationToken: cancellationToken));
    }
}
