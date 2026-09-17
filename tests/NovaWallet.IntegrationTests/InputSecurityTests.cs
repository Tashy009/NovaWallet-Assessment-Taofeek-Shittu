using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class InputSecurityTests(ApiFixture fixture)
{
    private const string DropTable = "'; DROP TABLE wallets; --";

    // SQL injection: a malicious narration is stored and returned as plain text, and the tables survive.
    [Fact]
    public async Task Sql_injection_in_narration_is_stored_as_plain_text()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(sender, source, destination, 1_000, narration: DropTable);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var statement = await sender.GetFromJsonAsync<StatementResponse>($"/api/v1/wallets/{source}/transactions");
        Assert.Equal(DropTable, statement!.Items[0].Narration);
        Assert.True(await CountAsync(fixture.Database, "SELECT count(*) FROM wallets", new { }) > 0);
    }

    // SQL injection: a malicious JWT sub is an opaque customer id that matches only itself.
    [Fact]
    public async Task Sql_injection_in_token_subject_matches_only_that_literal_customer()
    {
        var injectedId = $"' OR 1=1 -- {Guid.NewGuid():N}";
        using var attacker = fixture.CreateClient(injectedId);
        var (_, victimWallet) = await CreateFundedWalletAsync(fixture, 5_000);

        var created = await attacker.PostAsync("/api/v1/wallets", content: null);
        var victimBalance = await attacker.GetAsync($"/api/v1/wallets/{victimWallet}/balance");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(injectedId, (await created.Content.ReadFromJsonAsync<WalletResponse>())!.CustomerId);
        Assert.Equal(HttpStatusCode.NotFound, victimBalance.StatusCode);
    }

    // SQL injection: wallet ids and external references outside the allowed format never reach the database.
    [Fact]
    public async Task Sql_injection_in_wallet_id_or_external_reference_is_rejected()
    {
        var (owner, walletId) = await CreateFundedWalletAsync(fixture, 0);
        using var settlement = fixture.CreateSettlementClient();

        var byRoute = await owner.GetAsync($"/api/v1/wallets/{Uri.EscapeDataString(DropTable)}/balance");
        var byReference = await settlement.PostAsJsonAsync($"/api/v1/wallets/{walletId}/credits", new CreditWalletRequest(1_000, DropTable, null));

        Assert.Equal(HttpStatusCode.NotFound, byRoute.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, byReference.StatusCode);
        Assert.Equal(0, await GetBalanceAsync(owner, walletId));
    }

    // Oversized input: narration (140) and external reference (64) have hard length limits.
    [Fact]
    public async Task Oversized_narration_and_reference_are_rejected()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        using var settlement = fixture.CreateSettlementClient();

        var longNarration = await TransferAsync(sender, source, destination, 1_000, narration: new string('n', 141));
        var longReference = await settlement.PostAsJsonAsync($"/api/v1/wallets/{source}/credits",
            new CreditWalletRequest(1_000, new string('r', 65), null));

        Assert.Equal(HttpStatusCode.BadRequest, longNarration.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longReference.StatusCode);
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
    }

    // Malformed JSON, unknown fields and non-integer or out-of-range amounts return structured 400s, never 500s or overflow.
    [Theory]
    [InlineData("{")]
    [InlineData("""{"sourceWalletId":"{source}","destinationWalletId":"{destination}","amountKobo":1000,"isAdmin":true}""")]
    [InlineData("""{"sourceWalletId":"{source}","destinationWalletId":"{destination}","amountKobo":100.5}""")]
    [InlineData("""{"sourceWalletId":"{source}","destinationWalletId":"{destination}","amountKobo":"1000"}""")]
    [InlineData("""{"sourceWalletId":"{source}","destinationWalletId":"{destination}","amountKobo":9223372036854775807}""")]
    [InlineData("""{"sourceWalletId":"{source}","destinationWalletId":"{destination}","amountKobo":9223372036854775808}""")]
    [InlineData("""{"sourceWalletId":"{source}","destinationWalletId":"{destination}","amountKobo":1e30}""")]
    public async Task Hostile_transfer_payloads_return_structured_400(string template)
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = new StringContent(
                template.Replace("{source}", source.ToString()).Replace("{destination}", destination.ToString()),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await sender.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
    }

    // Information leakage: error bodies carry no stack traces, SQL, driver errors, ownership details or tokens.
    [Fact]
    public async Task Error_responses_do_not_leak_internals()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 1_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        using var intruder = fixture.CreateClient(ApiFixture.NewCustomerId());
        var token = sender.DefaultRequestHeaders.Authorization!.Parameter!;

        var responses = new[]
        {
            await TransferAsync(sender, source, destination, 5_000),
            await intruder.GetAsync($"/api/v1/wallets/{source}/balance"),
            await sender.PostAsync("/api/v1/transfers", new StringContent("{", Encoding.UTF8, "application/json")),
        };

        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(JsonSerializer.Deserialize<JsonElement>(body).TryGetProperty("status", out _));
            foreach (var forbidden in new[] { " at NovaWallet", "Npgsql", "SELECT ", "Password", "SigningKey", "customer_id", "cust-", token })
            {
                Assert.DoesNotContain(forbidden, body);
            }
        }

        Assert.Equal("Wallet was not found.", (await responses[1].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }
}
