using System.Net;
using System.Net.Http.Json;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class IdempotencyTests(ApiFixture fixture)
{
    // Idempotency: same key and payload replays the original result without a second debit.
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

    // Idempotency: same key with a different amount is rejected and moves nothing.
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

    // Idempotency: keys are scoped per customer, so two customers can use the same key.
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

    // Idempotency: a stored business rejection is replayed even after funds arrive.
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

    // CC5: 100 concurrent requests with one key execute exactly one transfer.
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

    // Idempotency: changing destination, source or narration under the same key is rejected.
    [Theory]
    [InlineData("destination")]
    [InlineData("source")]
    [InlineData("narration")]
    public async Task Same_key_with_changed_field_is_rejected(string changed)
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 100_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);
        var (_, otherWallet) = await CreateFundedWalletAsync(fixture, 50_000);
        var key = Guid.NewGuid().ToString("N");
        (await TransferAsync(sender, source, destination, 30_000, key, "Rent")).EnsureSuccessStatusCode();

        var reused = changed switch
        {
            "destination" => await TransferAsync(sender, source, otherWallet, 30_000, key, "Rent"),
            "source" => await TransferAsync(sender, otherWallet, destination, 30_000, key, "Rent"),
            _ => await TransferAsync(sender, source, destination, 30_000, key, "Rent - updated"),
        };

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", await ErrorCodeAsync(reused));
        Assert.Equal(70_000, await GetBalanceAsync(sender, source));
        Assert.Equal(30_000, await GetBalanceAsync(recipient, destination));
    }

    // Idempotency: a new key for an identical transfer is a new, separate operation.
    [Fact]
    public async Task Different_key_with_same_payload_is_a_new_transfer()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 100_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);

        var first = await TransferAsync(sender, source, destination, 30_000, "key-one");
        var second = await TransferAsync(sender, source, destination, 30_000, "key-two");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(
            (await first.Content.ReadFromJsonAsync<TransferResponse>())!.TransactionId,
            (await second.Content.ReadFromJsonAsync<TransferResponse>())!.TransactionId);
        Assert.Equal(40_000, await GetBalanceAsync(sender, source));
        Assert.Equal(60_000, await GetBalanceAsync(recipient, destination));
    }

    // Idempotency: empty, blank, oversized (>100) or badly formatted keys are rejected before anything moves.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("abc/def")]
    [InlineData("TOO_LONG")]
    public async Task Invalid_idempotency_keys_return_400(string key)
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = JsonContent.Create(new TransferRequest(source, destination, 1_000, "NGN", null)),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key == "TOO_LONG" ? new string('k', 101) : key);

        var response = await sender.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
    }

    // Idempotency: a 100-character key is the longest accepted.
    [Fact]
    public async Task Idempotency_key_of_exactly_100_characters_is_accepted()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(sender, source, destination, 1_000, $"{Guid.NewGuid():N}".PadRight(100, 'k'));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // CC6: concurrent requests sharing a key but carrying two different payloads execute exactly one transfer.
    [Fact]
    public async Task Concurrent_same_key_with_different_payloads_executes_only_one()
    {
        const int perPayload = 25;
        const long start = 1_000_000;
        var (sender, source) = await CreateFundedWalletAsync(fixture, start);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);
        var key = Guid.NewGuid().ToString("N");
        var amounts = Enumerable.Range(0, perPayload).SelectMany(_ => new long[] { 10_000, 20_000 }).ToList();

        var responses = await Task.WhenAll(amounts.Select(amount => TransferAsync(sender, source, destination, amount, key)));

        var winner = Assert.Single(amounts.Where((_, i) => responses[i].StatusCode == HttpStatusCode.Created).Distinct());
        Assert.Equal(perPayload, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var losers = responses.Where((_, i) => amounts[i] != winner).ToList();
        Assert.All(losers, r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        Assert.All(await Task.WhenAll(losers.Select(ErrorCodeAsync)), code => Assert.Equal("idempotency_key_reused", code));
        Assert.Equal(start - winner, await GetBalanceAsync(sender, source));
        Assert.Equal(winner, await GetBalanceAsync(recipient, destination));
        Assert.Equal(1, await CountAsync(fixture.Database,
            "SELECT count(*) FROM ledger_transactions WHERE source_wallet_id = @source", new { source }));
    }
}
