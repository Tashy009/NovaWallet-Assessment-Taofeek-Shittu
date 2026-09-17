using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api.Contracts;
using NovaWallet.Application.Abstractions;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class OutboxTests(ApiFixture fixture)
{
    private sealed record OutboxRow(Guid Id, Guid AggregateId, string Payload, DateTime? PublishedAt, int Attempts, string? LastError, DateTime NextAttemptAt);

    [Fact]
    public async Task Committed_transfer_writes_exactly_one_transfer_completed_event_with_minimal_payload()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 50_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        sender.DefaultRequestHeaders.Add("X-Correlation-ID", "outbox-corr-1");

        var receipt = await (await TransferAsync(sender, source, destination, 12_000, narration: "School fees"))
            .Content.ReadFromJsonAsync<TransferResponse>();

        var row = Assert.Single(await RowsForAsync(receipt!.TransactionId));
        Assert.Null(row.PublishedAt);
        Assert.Equal(0, row.Attempts);

        var payload = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(receipt.TransactionId, payload.GetProperty("transactionId").GetGuid());
        Assert.Equal(source, payload.GetProperty("sourceWalletId").GetGuid());
        Assert.Equal(destination, payload.GetProperty("destinationWalletId").GetGuid());
        Assert.Equal(12_000, payload.GetProperty("amountKobo").GetInt64());
        Assert.Equal("NGN", payload.GetProperty("currency").GetString());
        Assert.Equal("outbox-corr-1", payload.GetProperty("correlationId").GetString());
        Assert.Equal(row.Id, payload.GetProperty("eventId").GetGuid());
        Assert.False(payload.TryGetProperty("narration", out _));
        Assert.False(payload.TryGetProperty("sourceBalanceAfterKobo", out _));
    }

    [Fact]
    public async Task Rejected_transfer_writes_no_event()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 1_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(sender, source, destination, 5_000);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await CountEventsFromSourceAsync(source));
    }

    [Fact]
    public async Task Idempotent_replays_do_not_write_additional_events()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 50_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var key = Guid.NewGuid().ToString("N");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => TransferAsync(sender, source, destination, 1_000, key)));

        Assert.Equal(1, await CountEventsFromSourceAsync(source));
    }

    [Fact]
    public async Task Concurrent_overspend_attempts_produce_one_event_per_committed_transfer()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 100_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => TransferAsync(sender, source, destination, 10_000)));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(10, await CountEventsFromSourceAsync(source));
    }

    [Fact]
    public async Task Relay_publishes_pending_events_once_and_marks_them_published()
    {
        var transactionId = await CommittedTransferAsync();

        await DrainAsync();
        await DrainAsync();

        var row = Assert.Single(await RowsForAsync(transactionId));
        Assert.NotNull(row.PublishedAt);
        Assert.Equal(1, row.Attempts);
        var published = Assert.Single(fixture.Publisher.Published, e => e.AggregateId == transactionId);
        Assert.Equal("TransferCompleted", published.EventType);
        Assert.Equal(row.Id, published.EventId);
    }

    [Fact]
    public async Task Failed_publish_is_retried_later_with_backoff_and_then_delivered()
    {
        var transactionId = await CommittedTransferAsync();
        fixture.Publisher.FailingAggregates[transactionId] = true;

        try
        {
            await DrainAsync();

            var failed = Assert.Single(await RowsForAsync(transactionId));
            Assert.Null(failed.PublishedAt);
            Assert.Equal(1, failed.Attempts);
            Assert.Contains("Simulated broker outage", failed.LastError);
            Assert.True(failed.NextAttemptAt > DateTime.UtcNow, "Failed message must not be retried immediately.");
            Assert.DoesNotContain(fixture.Publisher.Published, e => e.AggregateId == transactionId);
        }
        finally
        {
            fixture.Publisher.FailingAggregates.TryRemove(transactionId, out _);
        }

        await using (var connection = await fixture.Database.OpenConnectionAsync())
        {
            // Simulate the backoff window elapsing.
            await connection.ExecuteAsync("UPDATE outbox_messages SET next_attempt_at = now() WHERE aggregate_id = @transactionId", new { transactionId });
        }

        await DrainAsync();

        var delivered = Assert.Single(await RowsForAsync(transactionId));
        Assert.NotNull(delivered.PublishedAt);
        Assert.Equal(2, delivered.Attempts);
        Assert.Null(delivered.LastError);
        Assert.Single(fixture.Publisher.Published, e => e.AggregateId == transactionId);
    }

    [Fact]
    public async Task Concurrent_relays_never_publish_the_same_event_twice()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 1_000_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => TransferAsync(sender, source, destination, 1_000)));
        var transactionIds = (await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<TransferResponse>())!.TransactionId)))
            .ToHashSet();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(DrainAsync)));

        var published = fixture.Publisher.Published.Where(e => transactionIds.Contains(e.AggregateId)).ToList();
        Assert.Equal(40, published.Count);
        Assert.Equal(40, published.Select(e => e.EventId).Distinct().Count());
        Assert.DoesNotContain(fixture.Publisher.Published.GroupBy(e => e.EventId), g => g.Count() > 1);
    }

    private async Task<Guid> CommittedTransferAsync()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 10_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var response = await TransferAsync(sender, source, destination, 2_500);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TransferResponse>())!.TransactionId;
    }

    private async Task DrainAsync()
    {
        var processor = fixture.Factory.Services.GetRequiredService<IOutboxProcessor>();
        while (await processor.PublishPendingAsync(CancellationToken.None) > 0)
        {
        }
    }

    private async Task<List<OutboxRow>> RowsForAsync(Guid transactionId)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        return (await connection.QueryAsync<OutboxRow>(
            """
            SELECT id AS Id, aggregate_id AS AggregateId, payload::text AS Payload, published_at AS PublishedAt,
                   attempts AS Attempts, last_error AS LastError, next_attempt_at AS NextAttemptAt
            FROM outbox_messages WHERE event_type = 'TransferCompleted' AND aggregate_id = @transactionId
            """,
            new { transactionId })).AsList();
    }

    private Task<long> CountEventsFromSourceAsync(Guid source) =>
        CountAsync(fixture.Database,
            "SELECT count(*) FROM outbox_messages WHERE event_type = 'TransferCompleted' AND payload->>'sourceWalletId' = @source",
            new { source = source.ToString() });
}
