using System.Diagnostics;
using System.Net;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class TransferConcurrencyTests(ApiFixture fixture)
{
    [Fact]
    public async Task Hundred_concurrent_transfers_cannot_overspend_the_source_balance()
    {
        const int requests = 100;
        var (sender, source) = await CreateFundedWalletAsync(fixture, 100_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);

        var responses = await Task.WhenAll(Enumerable.Range(0, requests)
            .Select(_ => TransferAsync(sender, source, destination, 10_000)));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var rejected = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(90, rejected.Count);
        Assert.All(rejected, r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        Assert.All(await Task.WhenAll(rejected.Select(ErrorCodeAsync)), code => Assert.Equal("insufficient_funds", code));

        Assert.Equal(0, await GetBalanceAsync(sender, source));
        Assert.Equal(100_000, await GetBalanceAsync(recipient, destination));
        await AssertWalletLedgerConsistentAsync(fixture.Database, source);
        await AssertWalletLedgerConsistentAsync(fixture.Database, destination);
    }

    [Fact]
    public async Task Opposing_transfers_between_two_wallets_do_not_deadlock_or_lose_money()
    {
        const int perDirection = 50;
        const long start = 1_000_000;
        var (ownerA, walletA) = await CreateFundedWalletAsync(fixture, start);
        var (ownerB, walletB) = await CreateFundedWalletAsync(fixture, start);

        var stopwatch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, perDirection).SelectMany(i => new[]
        {
            TransferAsync(ownerA, walletA, walletB, 1_000 + i),
            TransferAsync(ownerB, walletB, walletA, 1_000 + i),
        }));
        stopwatch.Stop();

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        // A deadlock would surface as a ~1s deadlock_timeout per victim plus retries; ordered locking has none.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Opposing transfers took {stopwatch.Elapsed}.");

        Assert.Equal(start, await GetBalanceAsync(ownerA, walletA));
        Assert.Equal(start, await GetBalanceAsync(ownerB, walletB));
        await AssertWalletLedgerConsistentAsync(fixture.Database, walletA);
        await AssertWalletLedgerConsistentAsync(fixture.Database, walletB);
    }

    [Fact]
    public async Task Concurrent_credits_and_transfers_on_one_wallet_keep_the_ledger_consistent()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 50_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        using var settlement = fixture.CreateSettlementClient();

        var transfers = Enumerable.Range(0, 40).Select(_ => TransferAsync(sender, source, destination, 2_000));
        var credits = Enumerable.Range(0, 40).Select(_ => System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
            settlement, $"/api/v1/wallets/{source}/credits",
            new NovaWallet.Api.Contracts.CreditWalletRequest(1_000, $"NIP-{Guid.NewGuid():N}", null)));

        var responses = await Task.WhenAll(transfers.Concat(credits));

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity, $"Unexpected {r.StatusCode}"));
        var succeededTransfers = responses.Take(40).Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(50_000 + 40 * 1_000 - succeededTransfers * 2_000, await GetBalanceAsync(sender, source));
        await AssertWalletLedgerConsistentAsync(fixture.Database, source);
        await AssertWalletLedgerConsistentAsync(fixture.Database, destination);
    }
}
