using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovaWallet.Application.Common;
using NovaWallet.Application.Credits;
using NovaWallet.Application.Transfers;
using NovaWallet.Application.Wallets;

namespace NovaWallet.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IConcurrencyRetryPolicy, ConcurrencyRetryPolicy>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<ICreditService, CreditService>();
        services.AddScoped<ITransferService, TransferService>();
        return services;
    }
}
