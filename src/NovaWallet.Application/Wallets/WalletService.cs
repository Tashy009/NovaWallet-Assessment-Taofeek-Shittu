using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;
using NovaWallet.Domain;

namespace NovaWallet.Application.Wallets;

public sealed record WalletView(
    Guid WalletId,
    string CustomerId,
    string Currency,
    long BalanceKobo,
    long DailyOutboundLimitKobo,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record BalanceView(Guid WalletId, string Currency, long BalanceKobo);

public sealed class WalletService(IWalletStore wallets)
{
    public async Task<Result<WalletView>> CreateAsync(Caller caller, CancellationToken cancellationToken)
    {
        var created = await wallets.TryCreateAsync(
            Guid.NewGuid(), caller.CustomerId, LedgerLimits.DefaultDailyOutboundLimitKobo, cancellationToken);

        if (created is not null)
        {
            return ToView(created);
        }

        var existing = await wallets.FindByCustomerAsync(caller.CustomerId, Currency.Ngn, cancellationToken)
            ?? throw new InvalidOperationException("Wallet creation conflicted but no existing wallet was found.");

        return Errors.WalletAlreadyExists(existing.Id);
    }

    public async Task<Result<BalanceView>> GetBalanceAsync(Caller caller, Guid walletId, CancellationToken cancellationToken)
    {
        var wallet = await wallets.FindAsync(walletId, cancellationToken);

        // Another customer's wallet is reported as not found so wallet ids cannot be probed.
        if (wallet is null || wallet.CustomerId != caller.CustomerId)
        {
            return Errors.WalletNotFound();
        }

        return new BalanceView(wallet.Id, wallet.Currency, wallet.BalanceKobo);
    }

    private static WalletView ToView(WalletSnapshot wallet) =>
        new(wallet.Id, wallet.CustomerId, wallet.Currency, wallet.BalanceKobo, wallet.DailyOutboundLimitKobo,
            wallet.Status, Utc(wallet.CreatedAt));

    internal static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
