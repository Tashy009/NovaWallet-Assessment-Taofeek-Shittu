using System.Net;
using NovaWallet.IntegrationTests.Infrastructure;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class HealthAndDocsTests(ApiFixture fixture)
{
    [Fact]
    public async Task Liveness_returns_200()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_returns_200_when_database_is_reachable()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("NovaWallet Ledger Service", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
