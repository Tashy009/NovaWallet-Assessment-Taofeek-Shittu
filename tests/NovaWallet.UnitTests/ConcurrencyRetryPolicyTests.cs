using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;

namespace NovaWallet.UnitTests;

public class ConcurrencyRetryPolicyTests
{
    private readonly ConcurrencyRetryPolicy _policy = new();

    [Fact]
    public async Task Retries_transient_failures_and_returns_the_eventual_result()
    {
        var attempts = 0;

        var result = await _policy.ExecuteAsync(_ =>
        {
            attempts++;
            return attempts < 3 ? throw Transient() : Task.FromResult("committed");
        }, CancellationToken.None);

        Assert.Equal("committed", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_surfaces_the_transient_failure()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TransientConcurrencyException>(() => _policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw Transient();
        }, CancellationToken.None));

        Assert.Equal(ConcurrencyRetryPolicy.MaxAttempts, attempts);
    }

    [Fact]
    public async Task Does_not_retry_non_transient_failures()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<LedgerInvariantViolationException>(() => _policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new LedgerInvariantViolationException("boom");
        }, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    private static TransientConcurrencyException Transient() => new(new InvalidOperationException("40P01"));
}
