using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.IntegrationTests.Infrastructure;
using Serilog.Core;
using Serilog.Events;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class LoggingSecurityTests(ApiFixture fixture)
{
    // Logging security: bearer tokens never reach the logs, while the IDOR attempt and its error code are recorded.
    [Fact]
    public async Task Logs_never_contain_tokens_but_record_cross_customer_attempts_with_error_code()
    {
        var sink = new CapturingSink();
        await using var host = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var intruderId = ApiFixture.NewCustomerId();
        using var owner = ApiFixture.CreateClientFor(host, ApiFixture.NewCustomerId());
        using var intruder = ApiFixture.CreateClientFor(host, intruderId);
        var walletId = await LedgerAssertions.CreateWalletAsync(owner);

        var response = await intruder.GetAsync($"/api/v1/wallets/{walletId}/balance");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var completion = await sink.WaitForAsync(e =>
            e.Properties.ContainsKey("Elapsed") && e.Properties.ContainsKey("StatusCode")
            && e.Properties.TryGetValue("RequestPath", out var path) && Scalar(path) == $"/api/v1/wallets/{walletId}/balance");
        Assert.Equal("wallet_not_found", Scalar(completion.Properties["ErrorCode"]));
        Assert.True(completion.Properties.ContainsKey("CorrelationId"));

        var warning = Assert.Single(sink.Events, e =>
            e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("owned by another customer"));
        Assert.Equal(intruderId, Scalar(warning.Properties["CustomerId"]));
        Assert.Equal(walletId.ToString(), Scalar(warning.Properties["WalletId"]));

        var tokens = new[] { owner, intruder }.Select(c => c.DefaultRequestHeaders.Authorization!.Parameter!).ToList();
        foreach (var logEvent in sink.Events)
        {
            var text = logEvent.RenderMessage() + string.Join(" ", logEvent.Properties.Values) + logEvent.Exception;
            Assert.DoesNotContain(tokens, token => text.Contains(token, StringComparison.Ordinal));
            Assert.DoesNotContain("Bearer ", text);
        }
    }

    private static string? Scalar(LogEventPropertyValue value) => (value as ScalarValue)?.Value?.ToString();

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

        // The request completion event is written as the pipeline unwinds, which can be just after the client has the response.
        public async Task<LogEvent> WaitForAsync(Func<LogEvent, bool> predicate)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (_events.FirstOrDefault(predicate) is { } match)
                {
                    return match;
                }

                await Task.Delay(100);
            }

            throw new Xunit.Sdk.XunitException("Expected log event was not written.");
        }
    }
}
