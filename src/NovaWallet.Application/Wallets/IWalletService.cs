using NovaWallet.Application.Common;

namespace NovaWallet.Application.Wallets;

public interface IWalletService
{
    Task<Result<WalletView>> CreateAsync(Caller caller, CancellationToken cancellationToken);

    Task<Result<BalanceView>> GetBalanceAsync(Caller caller, Guid walletId, CancellationToken cancellationToken);
}

public sealed record WalletView(
    Guid WalletId,
    string CustomerId,
    string Currency,
    long BalanceKobo,
    long DailyOutboundLimitKobo,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record BalanceView(Guid WalletId, string Currency, long BalanceKobo);
