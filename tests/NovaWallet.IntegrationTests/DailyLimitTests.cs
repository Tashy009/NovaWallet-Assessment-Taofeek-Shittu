using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DailyLimitTests(ApiFixture fixture)
{
    private const long DailyLimit = 50_000_000;

    // DL2/DL3: transfers adding up to exactly the limit succeed and one more kobo is rejected.
    [Fact]
    public async Task Transfers_up_to_exactly_the_limit_succeed_and_one_more_kobo_is_rejected()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 80_000_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        Assert.Equal(HttpStatusCode.Created, (await TransferAsync(sender, source, destination, 30_000_000)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await TransferAsync(sender, source, destination, 20_000_000)).StatusCode);
        var overLimit = await TransferAsync(sender, source, destination, 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, overLimit.StatusCode);
        var problem = await overLimit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("daily_limit_exceeded", problem.GetProperty("errorCode").GetString());
        Assert.Equal(DailyLimit, problem.GetProperty("dailyLimitKobo").GetInt64());
        Assert.Equal(0, problem.GetProperty("remainingTodayKobo").GetInt64());
        Assert.Equal(30_000_000, await GetBalanceAsync(sender, source));
    }

    // DL4: a transfer that would cross the limit is rejected in full.
    [Fact]
    public async Task Transfer_that_would_cross_the_limit_is_rejected_whole()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 80_000_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        await TransferAsync(sender, source, destination, 30_000_000);

        var response = await TransferAsync(sender, source, destination, 25_000_000);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("daily_limit_exceeded", await ErrorCodeAsync(response));
        Assert.Equal(50_000_000, await GetBalanceAsync(sender, source));
    }

    // Concurrency: parallel transfers never push the daily total past the limit.
    [Fact]
    public async Task Concurrent_transfers_never_exceed_the_daily_limit()
    {
        const int requests = 30;
        const long amount = 5_000_000;
        var (sender, source) = await CreateFundedWalletAsync(fixture, requests * amount);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);

        var responses = await Task.WhenAll(Enumerable.Range(0, requests)
            .Select(_ => TransferAsync(sender, source, destination, amount)));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(DailyLimit, await GetBalanceAsync(recipient, destination));
        Assert.Equal(DailyLimit, await CountAsync(fixture.Database,
            "SELECT COALESCE(sum(total_kobo), 0)::bigint FROM daily_outbound_totals WHERE wallet_id = @source", new { source }));
        await AssertWalletLedgerConsistentAsync(fixture.Database, source);
    }

    // Midnight WAT: the limit resets at 00:00 Africa/Lagos, not at UTC midnight.
    [Fact]
    public async Task Limit_resets_at_midnight_africa_lagos_not_utc()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 120_000_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        try
        {
            // 22:59:59 UTC = 23:59:59 WAT on 1 March.
            fixture.Clock.FixedUtcNow = new DateTimeOffset(2026, 3, 1, 22, 59, 59, TimeSpan.Zero);
            Assert.Equal(HttpStatusCode.Created, (await TransferAsync(sender, source, destination, DailyLimit)).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TransferAsync(sender, source, destination, 1)).StatusCode);

            // 23:00:00 UTC = 00:00:00 WAT on 2 March: a new business day, even though it is still 1 March in UTC.
            fixture.Clock.FixedUtcNow = new DateTimeOffset(2026, 3, 1, 23, 0, 0, TimeSpan.Zero);
            Assert.Equal(HttpStatusCode.Created, (await TransferAsync(sender, source, destination, DailyLimit)).StatusCode);
        }
        finally
        {
            fixture.Clock.FixedUtcNow = null;
        }

        Assert.Equal(20_000_000, await GetBalanceAsync(sender, source));
    }

    // DL1: a single transfer of exactly the daily limit succeeds.
    [Fact]
    public async Task Single_transfer_of_exactly_the_limit_succeeds()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, DailyLimit);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(sender, source, destination, DailyLimit);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await GetBalanceAsync(sender, source));
    }

    // DL5: a transfer rejected for insufficient funds does not use up any of the daily limit.
    [Fact]
    public async Task Failed_transfer_does_not_consume_the_daily_limit()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 30_000_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        using var settlement = fixture.CreateSettlementClient();

        var failed = await TransferAsync(sender, source, destination, 40_000_000);
        (await settlement.PostAsJsonAsync($"/api/v1/wallets/{source}/credits",
            new NovaWallet.Api.Contracts.CreditWalletRequest(40_000_000, $"NIP-{Guid.NewGuid():N}", null))).EnsureSuccessStatusCode();
        var fullLimit = await TransferAsync(sender, source, destination, DailyLimit);

        Assert.Equal("insufficient_funds", await ErrorCodeAsync(failed));
        Assert.Equal(HttpStatusCode.Created, fullLimit.StatusCode);
        Assert.Equal(20_000_000, await GetBalanceAsync(sender, source));
    }
}
