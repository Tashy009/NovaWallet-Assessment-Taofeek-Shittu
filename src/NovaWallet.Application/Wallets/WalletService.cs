using Microsoft.Extensions.Logging;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;
using NovaWallet.Domain;

namespace NovaWallet.Application.Wallets;

public sealed class WalletService(IWalletStore wallets, ILogger<WalletService> logger) : IWalletService
{
    public async Task<Result<WalletView>> CreateAsync(Caller caller, CancellationToken cancellationToken)
    {
        var created = await wallets.TryCreateAsync(
            Guid.NewGuid(), caller.CustomerId, LedgerLimits.DefaultDailyOutboundLimitKobo, cancellationToken);

        if (created is not null)
        {
            return new WalletView(created.Id, created.CustomerId, created.Currency, created.BalanceKobo,
                created.DailyOutboundLimitKobo, created.Status, UtcTime.From(created.CreatedAt));
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
            if (wallet is not null)
            {
                logger.LogWarning("Customer {CustomerId} requested balance of wallet {WalletId} owned by another customer",
                    caller.CustomerId, walletId);
            }

            return Errors.WalletNotFound();
        }

        return new BalanceView(wallet.Id, wallet.Currency, wallet.BalanceKobo);
    }
}
