using NovaWallet.Application.Common;

namespace NovaWallet.Application.Statements;

public interface IStatementService
{
    Task<Result<StatementPage>> GetPageAsync(
        Caller caller, Guid walletId, int? limit, string? cursor, CancellationToken cancellationToken);
}

public sealed record StatementItem(
    Guid TransactionId,
    string Type,
    string Direction,
    string Currency,
    long AmountKobo,
    long BalanceAfterKobo,
    Guid? CounterpartyWalletId,
    string? ExternalReference,
    string? Narration,
    DateTimeOffset CreatedAt);

public sealed record StatementPage(Guid WalletId, IReadOnlyList<StatementItem> Items, string? NextCursor);
