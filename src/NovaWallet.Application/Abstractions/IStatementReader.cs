namespace NovaWallet.Application.Abstractions;

public sealed record StatementEntryRecord(
    long EntryNo,
    Guid TransactionId,
    string Type,
    string Direction,
    long AmountKobo,
    long BalanceAfterKobo,
    Guid? CounterpartyWalletId,
    string? ExternalReference,
    string? Narration,
    DateTime CreatedAt);

public interface IStatementReader
{
    // Newest first: entries with entry_no < beforeEntryNo, ordered by entry_no descending.
    Task<IReadOnlyList<StatementEntryRecord>> ReadAsync(Guid walletId, long beforeEntryNo, int take, CancellationToken cancellationToken);
}
