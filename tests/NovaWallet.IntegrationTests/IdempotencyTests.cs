using System.Net;
using System.Net.Http.Json;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class IdempotencyTests(ApiFixture fixture)
{
    [Fact]
    public async Task Same_key_and_payload_returns_original_result_without_double_debit()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 100_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var key = Guid.NewGuid().ToString("N");

        var first = await TransferAsync(sender, source, destination, 30_000, key);
        var replay = await TransferAsync(sender, source, destination, 30_000, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadFromJsonAsync<TransferResponse>(), await replay.Content.ReadFromJsonAsync<TransferResponse>());
        Assert.Equal(70_000, await GetBalanceAsync(sender, source));
        await AssertWalletLedgerConsistentAsync(fixture.Database, source);
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_rejected_and_moves_nothing()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 100_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var key = Guid.NewGuid().ToString("N");
        await TransferAsync(sender, source, destination, 30_000, key);

        var reused = await TransferAsync(sender, source, destination, 60_000, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", await ErrorCodeAsync(reused));
        Assert.Equal(70_000, await GetBalanceAsync(sender, source));
    }

    [Fact]
    public async Task Same_key_used_by_different_customers_is_independent()
    {
        var (senderA, sourceA) = await CreateFundedWalletAsync(fixture, 10_000);
        var (senderB, sourceB) = await CreateFundedWalletAsync(fixture, 10_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        const string sharedKey = "order-123";

        var a = await TransferAsync(senderA, sourceA, destination, 1_000, sharedKey);
        var b = await TransferAsync(senderB, sourceB, destination, 1_000, sharedKey);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.False(b.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task Rejected_outcome_is_replayed_even_after_funds_arrive()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 1_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var key = Guid.NewGuid().ToString("N");

        var rejected = await TransferAsync(sender, source, destination, 5_000, key);
        using var settlement = fixture.CreateSettlementClient();
        await settlement.PostAsJsonAsync($"/api/v1/wallets/{source}/credits", new CreditWalletRequest(10_000, $"NIP-{Guid.NewGuid():N}", null));
        var replay = await TransferAsync(sender, source, destination, 5_000, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
        Assert.Equal("insufficient_funds", await ErrorCodeAsync(replay));
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(11_000, await GetBalanceAsync(sender, source));
    }

    [Fact]
    public async Task Hundred_concurrent_requests_with_same_key_execute_exactly_one_transfer()
    {
        const int requests = 100;
        var (sender, source) = await CreateFundedWalletAsync(fixture, 1_000_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, requests)
            .Select(_ => TransferAsync(sender, source, destination, 25_000, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(responses, r => !r.Headers.Contains("Idempotent-Replayed"));
        var transactionIds = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<TransferResponse>())!.TransactionId));
        Assert.Single(transactionIds.Distinct());

        Assert.Equal(975_000, await GetBalanceAsync(sender, source));
        Assert.Equal(25_000, await GetBalanceAsync(recipient, destination));
        Assert.Equal(1, await CountAsync(fixture.Database,
            "SELECT count(*) FROM ledger_transactions WHERE source_wallet_id = @source", new { source }));
        await AssertWalletLedgerConsistentAsync(fixture.Database, source);
        await AssertWalletLedgerConsistentAsync(fixture.Database, destination);
    }
}
