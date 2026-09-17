using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthenticationTests(ApiFixture fixture)
{
    // W5/Security: tokens that are expired, forged, for another issuer/audience or unsigned are rejected with 401.
    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-signature")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("unsigned-alg-none")]
    public async Task Invalid_tokens_are_rejected_with_401(string scenario)
    {
        var token = scenario switch
        {
            "expired" => TestTokens.Create(fixture, expiresUtc: DateTime.UtcNow.AddMinutes(-5)),
            "wrong-signature" => TestTokens.Create(fixture, signingKey: "attacker-controlled-signing-key-0123456789abcdef"),
            "wrong-issuer" => TestTokens.Create(fixture, issuer: "https://evil.example"),
            "wrong-audience" => TestTokens.Create(fixture, audience: "some-other-api"),
            _ => TestTokens.Unsigned(fixture, ApiFixture.NewCustomerId()),
        };
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/v1/wallets", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Control for the test above: the same token builder with correct settings is accepted.
    [Fact]
    public async Task Correctly_built_test_token_is_accepted()
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(fixture, ApiFixture.NewCustomerId()));

        var response = await client.PostAsync("/api/v1/wallets", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // W6: a validly signed token without a sub claim cannot act as a customer (403, never a 500).
    [Fact]
    public async Task Token_without_subject_is_forbidden_not_a_server_error()
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(fixture, subject: null));

        var response = await client.PostAsync("/api/v1/wallets", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // W4: every protected endpoint requires a token.
    [Theory]
    [InlineData("POST", "/api/v1/wallets")]
    [InlineData("GET", "/api/v1/wallets/00000000-0000-0000-0000-000000000001/balance")]
    [InlineData("GET", "/api/v1/wallets/00000000-0000-0000-0000-000000000001/transactions")]
    [InlineData("POST", "/api/v1/wallets/00000000-0000-0000-0000-000000000001/credits")]
    [InlineData("POST", "/api/v1/transfers")]
    public async Task Protected_endpoints_return_401_without_a_token(string method, string url)
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method == "POST" ? JsonContent.Create(new { }) : null,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Security: a signing key shorter than 32 bytes stops the application from starting.
    [Fact]
    public void Weak_signing_key_prevents_startup()
    {
        using var weak = fixture.Factory.WithWebHostBuilder(builder => builder.UseSetting("Jwt:SigningKey", "too-short"));

        var ex = Assert.ThrowsAny<Exception>(() => weak.Server);

        Assert.Contains("at least 32 bytes", Flatten(ex));
    }

    // C8: customer tokens, even with other scopes and on their own wallet, cannot mint money; the ledger:credit scope can.
    [Fact]
    public async Task Only_the_ledger_credit_scope_can_post_credits()
    {
        var (owner, walletId) = await LedgerAssertions.CreateFundedWalletAsync(fixture, 0);
        using var customerWithOtherScope = fixture.CreateClient(ApiFixture.NewCustomerId(), "wallet:read");
        using var settlement = fixture.CreateSettlementClient();
        var url = $"/api/v1/wallets/{walletId}/credits";

        var byOwner = await owner.PostAsJsonAsync(url, new CreditWalletRequest(1_000_000, $"NIP-{Guid.NewGuid():N}", null));
        var byOtherScope = await customerWithOtherScope.PostAsJsonAsync(url, new CreditWalletRequest(1_000_000, $"NIP-{Guid.NewGuid():N}", null));
        var bySettlement = await settlement.PostAsJsonAsync(url, new CreditWalletRequest(1_000, $"NIP-{Guid.NewGuid():N}", null));

        Assert.Equal(HttpStatusCode.Forbidden, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byOtherScope.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bySettlement.StatusCode);
        Assert.Equal(1_000, await LedgerAssertions.GetBalanceAsync(owner, walletId));
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        var pending = new Stack<Exception>([ex]);
        while (pending.TryPop(out var current))
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                aggregate.InnerExceptions.ToList().ForEach(pending.Push);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return string.Join(" | ", messages);
    }
}
