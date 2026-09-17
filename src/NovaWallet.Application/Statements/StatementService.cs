using Microsoft.Extensions.Logging;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;
using NovaWallet.Domain;

namespace NovaWallet.Application.Statements;

public sealed class StatementService(IWalletStore wallets, IStatementReader reader, ILogger<StatementService> logger) : IStatementService
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public async Task<Result<StatementPage>> GetPageAsync(
        Caller caller, Guid walletId, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var pageSize = limit ?? DefaultLimit;
        if (pageSize is < 1 or > MaxLimit)
        {
            errors.Add("limit", $"Must be between 1 and {MaxLimit}.");
        }

        var before = long.MaxValue;
        if (cursor is not null && !StatementCursor.TryDecode(cursor, out before))
        {
            errors.Add("cursor", "Cursor is invalid.");
        }

        if (errors.HasErrors)
        {
            return errors.ToError();
        }

        var wallet = await wallets.FindAsync(walletId, cancellationToken);
        if (wallet is null || wallet.CustomerId != caller.CustomerId)
        {
            if (wallet is not null)
            {
                logger.LogWarning("Customer {CustomerId} requested statement of wallet {WalletId} owned by another customer",
                    caller.CustomerId, walletId);
            }

            return Errors.WalletNotFound();
        }

        // One extra row tells us whether another page exists without a count query.
        var rows = await reader.ReadAsync(walletId, before, pageSize + 1, cancellationToken);
        var page = rows.Take(pageSize).ToList();
        var nextCursor = rows.Count > pageSize ? StatementCursor.Encode(page[^1].EntryNo) : null;

        var items = page
            .Select(r => new StatementItem(r.TransactionId, r.Type, r.Direction, Currency.Ngn, r.AmountKobo, r.BalanceAfterKobo,
                r.CounterpartyWalletId, r.ExternalReference, r.Narration, UtcTime.From(r.CreatedAt)))
            .ToList();

        return new StatementPage(walletId, items, nextCursor);
    }
}
