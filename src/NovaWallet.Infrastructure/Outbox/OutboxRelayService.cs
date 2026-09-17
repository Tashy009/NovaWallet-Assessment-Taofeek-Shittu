using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Outbox;

internal sealed class OutboxRelayService(
    IServiceProvider services,
    IOptions<OutboxOptions> options,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RelayEnabled)
        {
            logger.LogInformation("Outbox relay is disabled");
            return;
        }

        var processor = services.GetRequiredService<IOutboxProcessor>();
        var idleDelay = TimeSpan.FromMilliseconds(options.Value.PollIntervalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Keep draining while there is backlog; sleep only when a poll comes back empty.
                if (await processor.PublishPendingAsync(stoppingToken) > 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox relay iteration failed");
            }

            try
            {
                await Task.Delay(idleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
