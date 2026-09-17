using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class CreditTests(ApiFixture fixture)
{
    [Fact]
    public async Task Valid_credit_returns_201_and_increases_balance()
    {
        var (owner, walletId) = await NewWalletAsync();
        using var settlement = fixture.CreateSettlementClient();

        var response = await settlement.PostAsJsonAsync(CreditsUrl(walletId), new CreditWalletRequest(250_000, NewReference(), "Inbound NIP"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<CreditResponse>();
        Assert.Equal(250_000, receipt!.AmountKobo);
        Assert.Equal(250_000, receipt.BalanceAfterKobo);
        Assert.Equal(250_000, await LedgerAssertions.GetBalanceAsync(owner, walletId));
        await LedgerAssertions.AssertWalletLedgerConsistentAsync(fixture.Database, walletId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000_000_001)]
    public async Task Invalid_amount_returns_400_and_changes_nothing(long amountKobo)
    {
        var (owner, walletId) = await NewWalletAsync();
        using var settlement = fixture.CreateSettlementClient();

        var response = await settlement.PostAsJsonAsync(CreditsUrl(walletId), new CreditWalletRequest(amountKobo, NewReference(), null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("amountKobo", out _));
        Assert.Equal(0, await LedgerAssertions.GetBalanceAsync(owner, walletId));
    }

    [Theory]
    [InlineData("""{"amountKobo": 100.5, "externalReference": "NIP-1"}""")]
    [InlineData("""{"amountKobo": "100", "externalReference": "NIP-1"}""")]
    [InlineData("""{"amountKobo": 100, "externalReference": "NIP-1", "currency": "USD"}""")]
    [InlineData("""{"amountKobo": 100}""")]
    [InlineData("not json")]
    public async Task Malformed_or_non_integer_payload_returns_400(string body)
    {
        var (owner, walletId) = await NewWalletAsync();
        using var settlement = fixture.CreateSettlementClient();

        var response = await settlement.PostAsync(CreditsUrl(walletId), new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await LedgerAssertions.GetBalanceAsync(owner, walletId));
    }

    [Fact]
    public async Task Customer_without_credit_scope_cannot_credit_even_own_wallet()
    {
        var (owner, walletId) = await NewWalletAsync();

        var response = await owner.PostAsJsonAsync(CreditsUrl(walletId), new CreditWalletRequest(1_000, NewReference(), null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await LedgerAssertions.GetBalanceAsync(owner, walletId));
    }

    [Fact]
    public async Task Credit_to_unknown_wallet_returns_404_and_writes_nothing()
    {
        using var settlement = fixture.CreateSettlementClient();
        var reference = NewReference();

        var response = await settlement.PostAsJsonAsync(CreditsUrl(Guid.NewGuid()), new CreditWalletRequest(1_000, reference, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM ledger_transactions WHERE external_reference = @reference", new { reference }));
    }

    [Fact]
    public async Task Replaying_same_reference_and_payload_returns_original_receipt_without_double_credit()
    {
        var (owner, walletId) = await NewWalletAsync();
        using var settlement = fixture.CreateSettlementClient();
        var request = new CreditWalletRequest(10_000, NewReference(), "Salary");

        var first = await settlement.PostAsJsonAsync(CreditsUrl(walletId), request);
        var replay = await settlement.PostAsJsonAsync(CreditsUrl(walletId), request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadFromJsonAsync<CreditResponse>(), await replay.Content.ReadFromJsonAsync<CreditResponse>());
        Assert.Equal(10_000, await LedgerAssertions.GetBalanceAsync(owner, walletId));
        await LedgerAssertions.AssertWalletLedgerConsistentAsync(fixture.Database, walletId);
    }

    [Fact]
    public async Task Reusing_reference_with_different_amount_returns_409_and_changes_nothing()
    {
        var (owner, walletId) = await NewWalletAsync();
        using var settlement = fixture.CreateSettlementClient();
        var reference = NewReference();
        await settlement.PostAsJsonAsync(CreditsUrl(walletId), new CreditWalletRequest(10_000, reference, null));

        var response = await settlement.PostAsJsonAsync(CreditsUrl(walletId), new CreditWalletRequest(99_000, reference, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("external_reference_conflict",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal(10_000, await LedgerAssertions.GetBalanceAsync(owner, walletId));
    }

    [Fact]
    public async Task Concurrent_distinct_credits_are_all_applied_with_no_lost_updates()
    {
        const int credits = 50;
        const long amount = 1_000;
        var (owner, walletId) = await NewWalletAsync();
        using var settlement = fixture.CreateSettlementClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, credits).Select(_ =>
            settlement.PostAsJsonAsync(CreditsUrl(walletId), new CreditWalletRequest(amount, NewReference(), null))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(credits * amount, await LedgerAssertions.GetBalanceAsync(owner, walletId));
        await LedgerAssertions.AssertWalletLedgerConsistentAsync(fixture.Database, walletId);
    }

    [Fact]
    public async Task Concurrent_duplicates_of_one_reference_credit_exactly_once()
    {
        const int attempts = 20;
        var (owner, walletId) = await NewWalletAsync();
        using var settlement = fixture.CreateSettlementClient();
        var request = new CreditWalletRequest(7_500, NewReference(), null);

        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ =>
            settlement.PostAsJsonAsync(CreditsUrl(walletId), request)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(responses, r => !r.Headers.Contains("Idempotent-Replayed"));
        var transactionIds = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<CreditResponse>())!.TransactionId));
        Assert.Single(transactionIds.Distinct());
        Assert.Equal(7_500, await LedgerAssertions.GetBalanceAsync(owner, walletId));
        await LedgerAssertions.AssertWalletLedgerConsistentAsync(fixture.Database, walletId);
    }

    private async Task<(HttpClient Owner, Guid WalletId)> NewWalletAsync()
    {
        var owner = fixture.CreateClient(ApiFixture.NewCustomerId());
        return (owner, await LedgerAssertions.CreateWalletAsync(owner));
    }

    private static string CreditsUrl(Guid walletId) => $"/api/v1/wallets/{walletId}/credits";

    private static string NewReference() => $"NIP-{Guid.NewGuid():N}";
}
