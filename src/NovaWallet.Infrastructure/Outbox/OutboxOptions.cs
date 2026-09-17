namespace NovaWallet.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public bool RelayEnabled { get; init; } = true;

    public int PollIntervalMilliseconds { get; init; } = 2000;

    public int BatchSize { get; init; } = 100;

    public int MaxBackoffSeconds { get; init; } = 300;
}
