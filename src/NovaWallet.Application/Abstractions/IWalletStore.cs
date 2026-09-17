namespace NovaWallet.Application.Abstractions;

public interface IWalletStore
{
    // Returns null when the customer already has a wallet in that currency.
    Task<WalletSnapshot?> TryCreateAsync(Guid walletId, string customerId, long dailyOutboundLimitKobo, CancellationToken cancellationToken);

    Task<WalletSnapshot?> FindAsync(Guid walletId, CancellationToken cancellationToken);

    Task<WalletSnapshot?> FindByCustomerAsync(string customerId, string currency, CancellationToken cancellationToken);
}
