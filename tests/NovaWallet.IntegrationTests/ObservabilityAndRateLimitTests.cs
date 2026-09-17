using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ObservabilityAndRateLimitTests(ApiFixture fixture)
{
    // Audit: the caller's correlation id is echoed and stored on ledger and audit rows.
    [Fact]
    public async Task Supplied_correlation_id_is_echoed_and_recorded_on_ledger_and_audit_rows()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 10_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var correlationId = $"mobile-{Guid.NewGuid():N}";
        sender.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);

        var response = await TransferAsync(sender, source, destination, 1_000);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
        var transactionId = (await response.Content.ReadFromJsonAsync<TransferResponse>())!.TransactionId;

        await using var connection = await fixture.Database.OpenConnectionAsync();
        Assert.Equal(correlationId, await connection.ExecuteScalarAsync<string>(
            "SELECT correlation_id FROM ledger_transactions WHERE id = @transactionId", new { transactionId }));
        Assert.All(await connection.QueryAsync<string>(
                "SELECT correlation_id FROM audit_log WHERE transaction_id = @transactionId", new { transactionId }),
            id => Assert.Equal(correlationId, id));
    }

    // Logging security: missing or unsafe correlation ids are replaced, preventing log injection.
    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("inject\"},{\"level\":\"Fatal")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Missing_or_unsafe_correlation_id_is_replaced_with_a_generated_one(string supplied)
    {
        using var client = fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", supplied);

        var response = await client.SendAsync(request);

        var echoed = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.NotEqual(supplied, echoed);
        Assert.Matches("^[A-Za-z0-9_.:-]{1,64}$", echoed);
    }

    // Errors: problem details include the correlation id for support.
    [Fact]
    public async Task Problem_details_include_the_correlation_id()
    {
        using var client = fixture.CreateClient(ApiFixture.NewCustomerId());
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "support-ticket-42");

        var response = await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}/balance");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("support-ticket-42", problem.GetProperty("correlationId").GetString());
    }

    // Security: transfers are rate limited per customer with a 429 problem response.
    [Fact]
    public async Task Transfer_endpoint_is_rate_limited_per_customer_with_problem_details()
    {
        await using var limited = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Transfers:PermitLimit", "3");
            builder.UseSetting("RateLimiting:Transfers:WindowSeconds", "60");
        });
        var issuer = limited.Services.GetRequiredService<DevTokenIssuer>();
        using var alice = ClientFor(limited, issuer, ApiFixture.NewCustomerId());
        using var bob = ClientFor(limited, issuer, ApiFixture.NewCustomerId());

        // Missing Idempotency-Key => cheap 400s that still consume the limiter's permits and never touch balances.
        var aliceResponses = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
        {
            aliceResponses.Add(await alice.PostAsJsonAsync("/api/v1/transfers",
                new TransferRequest(Guid.NewGuid(), Guid.NewGuid(), 100, "NGN", null)));
        }

        var bobResponse = await bob.PostAsJsonAsync("/api/v1/transfers", new TransferRequest(Guid.NewGuid(), Guid.NewGuid(), 100, "NGN", null));

        Assert.All(aliceResponses.Take(3), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        var throttled = aliceResponses[3];
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("application/problem+json", throttled.Content.Headers.ContentType?.MediaType);
        Assert.True(throttled.Headers.RetryAfter is not null || throttled.Headers.Contains("Retry-After"));
        Assert.Equal("rate_limited", (await throttled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bobResponse.StatusCode);
    }

    private static HttpClient ClientFor(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, DevTokenIssuer issuer, string customerId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.Issue(customerId, []).AccessToken);
        return client;
    }
}
