using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api.Auth;

namespace NovaWallet.IntegrationTests.Infrastructure;

public sealed class ApiFixture : IAsyncLifetime
{
    public const string CreditScope = "ledger:credit";

    private PostgresDatabase? _database;
    private NovaWalletApiFactory? _factory;

    public PostgresDatabase Database => _database ?? throw new InvalidOperationException("Fixture not initialised.");

    public NovaWalletApiFactory Factory => _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    public TestClock Clock { get; } = new();

    public static string NewCustomerId() => $"cust-{Guid.NewGuid():N}";

    public HttpClient CreateClient(string customerId, params string[] scopes)
    {
        var (token, _) = Factory.Services.GetRequiredService<DevTokenIssuer>().Issue(customerId, scopes);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // The internal NIP settlement service identity that is allowed to post credits.
    public HttpClient CreateSettlementClient() => CreateClient("svc-nip-settlement", CreditScope);

    public async Task InitializeAsync()
    {
        _database = await PostgresDatabase.CreateAsync();
        _factory = new NovaWalletApiFactory(_database.ConnectionString, Clock);

        // Force host start-up now so migrations run once, before any test.
        _ = _factory.Server;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}
