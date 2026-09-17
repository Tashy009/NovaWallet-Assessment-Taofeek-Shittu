using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NovaWallet.IntegrationTests.Infrastructure;

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset? FixedUtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => FixedUtcNow ?? base.GetUtcNow();
}

public sealed class NovaWalletApiFactory(string connectionString, TestClock clock) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Ledger", connectionString);
        builder.UseSetting("Database:RunMigrationsOnStartup", "true");
        builder.UseSetting("Swagger:Enabled", "true");
        builder.UseSetting("DevAuth:Enabled", "true");
        builder.UseSetting("Jwt:SigningKey", "integration-tests-only-signing-key-0123456789abcdef");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
    }
}
