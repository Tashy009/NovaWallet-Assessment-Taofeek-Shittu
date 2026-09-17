using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class WalletTests(ApiFixture fixture)
{
    // W1: a new wallet returns 201 with zero balance, NGN and the token subject as owner.
    [Fact]
    public async Task Create_wallet_returns_201_with_zero_ngn_balance_owned_by_token_subject()
    {
        var customerId = ApiFixture.NewCustomerId();
        using var client = fixture.CreateClient(customerId);

        var response = await client.PostAsync("/api/v1/wallets", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var wallet = await response.Content.ReadFromJsonAsync<WalletResponse>();
        Assert.NotNull(wallet);
        Assert.Equal(customerId, wallet.CustomerId);
        Assert.Equal("NGN", wallet.Currency);
        Assert.Equal(0, wallet.BalanceKobo);
        Assert.Equal(50_000_000, wallet.DailyOutboundLimitKobo);
        Assert.Equal("ACTIVE", wallet.Status);
        Assert.Equal($"/api/v1/wallets/{wallet.WalletId}/balance", response.Headers.Location?.AbsolutePath);
    }

    // B1: a new wallet's balance is zero.
    [Fact]
    public async Task Get_balance_of_new_wallet_is_zero()
    {
        using var client = fixture.CreateClient(ApiFixture.NewCustomerId());
        var walletId = await LedgerAssertions.CreateWalletAsync(client);

        var balance = await client.GetFromJsonAsync<BalanceResponse>($"/api/v1/wallets/{walletId}/balance");

        Assert.Equal(new BalanceResponse(walletId, "NGN", 0), balance);
    }

    // W2: a second NGN wallet for the same customer returns 409 with the existing wallet id.
    [Fact]
    public async Task Second_wallet_for_same_customer_returns_409_with_existing_wallet_id()
    {
        using var client = fixture.CreateClient(ApiFixture.NewCustomerId());
        var walletId = await LedgerAssertions.CreateWalletAsync(client);

        var response = await client.PostAsync("/api/v1/wallets", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("wallet_already_exists", problem.GetProperty("errorCode").GetString());
        Assert.Equal(walletId, problem.GetProperty("walletId").GetGuid());
    }

    // W2: concurrent creates for one customer produce exactly one wallet (unique constraint).
    [Fact]
    public async Task Concurrent_creates_for_same_customer_produce_exactly_one_wallet()
    {
        using var client = fixture.CreateClient(ApiFixture.NewCustomerId());

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PostAsync("/api/v1/wallets", content: null)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    // B5: a nonexistent wallet returns 404 problem details.
    [Fact]
    public async Task Unknown_wallet_returns_404_problem_details()
    {
        using var client = fixture.CreateClient(ApiFixture.NewCustomerId());

        var response = await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}/balance");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wallet_not_found", (await ReadProblemAsync(response)).GetProperty("errorCode").GetString());
    }

    // B4/IDOR: another customer's wallet is reported as not found.
    [Fact]
    public async Task Another_customers_wallet_is_reported_as_not_found()
    {
        using var owner = fixture.CreateClient(ApiFixture.NewCustomerId());
        using var intruder = fixture.CreateClient(ApiFixture.NewCustomerId());
        var walletId = await LedgerAssertions.CreateWalletAsync(owner);

        var response = await intruder.GetAsync($"/api/v1/wallets/{walletId}/balance");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // W4/W5: missing or forged tokens are rejected with 401.
    [Fact]
    public async Task Requests_without_a_valid_token_are_rejected_with_401()
    {
        using var anonymous = fixture.Factory.CreateClient();
        using var forged = fixture.Factory.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.invalid");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/wallets", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.PostAsync("/api/v1/wallets", null)).StatusCode);
    }

    // Security: an oversized sub claim is rejected with 403 instead of a database error.
    [Fact]
    public async Task Validly_signed_token_with_oversized_subject_is_forbidden_not_a_server_error()
    {
        using var client = fixture.CreateClient(new string('x', 129));

        var response = await client.PostAsync("/api/v1/wallets", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Input: a wallet id that is not a GUID never reaches the database.
    [Fact]
    public async Task Non_guid_wallet_id_returns_404()
    {
        using var client = fixture.CreateClient(ApiFixture.NewCustomerId());

        var response = await client.GetAsync("/api/v1/wallets/not-a-guid/balance");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // W3: different customers each get their own wallet.
    [Fact]
    public async Task Different_customers_can_each_create_a_wallet()
    {
        using var customerA = fixture.CreateClient(ApiFixture.NewCustomerId());
        using var customerB = fixture.CreateClient(ApiFixture.NewCustomerId());

        var walletA = await customerA.PostAsync("/api/v1/wallets", content: null);
        var walletB = await customerB.PostAsync("/api/v1/wallets", content: null);

        Assert.Equal(HttpStatusCode.Created, walletA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, walletB.StatusCode);
        Assert.NotEqual(
            (await walletA.Content.ReadFromJsonAsync<WalletResponse>())!.WalletId,
            (await walletB.Content.ReadFromJsonAsync<WalletResponse>())!.WalletId);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
