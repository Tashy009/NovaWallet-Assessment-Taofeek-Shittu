using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api.Contracts;
using NovaWallet.Application.Abstractions;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

public enum FailurePoint
{
    DestinationCredit,
    Audit,
    Commit,
}

// Simulates a crash at a chosen step inside the ledger transaction by throwing from a wrapped unit of work.
[Collection(ApiCollection.Name)]
public class FailureAtomicityTests(ApiFixture fixture)
{
    private const string SimulatedFailure = "Simulated crash inside the ledger transaction";

    // DB1-DB4: a crash after the debit, before the audit or at commit rolls everything back, and the same key can be retried.
    [Theory]
    [InlineData(FailurePoint.DestinationCredit)]
    [InlineData(FailurePoint.Audit)]
    [InlineData(FailurePoint.Commit)]
    public async Task Transfer_failing_mid_transaction_leaves_no_partial_state_and_can_be_retried(FailurePoint failAt)
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 10_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);
        var customerId = await CustomerOfAsync(source);
        var key = Guid.NewGuid().ToString("N");

        await using (var faulty = FaultyHost(failAt))
        {
            using var faultyClient = ApiFixture.CreateClientFor(faulty, customerId);
            var failed = await TransferAsync(faultyClient, source, destination, 4_000, key);

            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            await AssertNoInternalsLeakedAsync(failed);
        }

        Assert.Equal(10_000, await GetBalanceAsync(sender, source));
        Assert.Equal(0, await GetBalanceAsync(recipient, destination));
        Assert.Equal(0, await CountAsync(fixture.Database,
            "SELECT count(*) FROM ledger_transactions WHERE source_wallet_id = @source", new { source }));
        Assert.Equal(0, await CountAsync(fixture.Database,
            "SELECT count(*) FROM audit_log WHERE wallet_id = @destination", new { destination }));
        Assert.Equal(0, await CountAsync(fixture.Database,
            "SELECT COALESCE(sum(total_kobo), 0)::bigint FROM daily_outbound_totals WHERE wallet_id = @source", new { source }));
        Assert.Equal(0, await CountAsync(fixture.Database,
            "SELECT count(*) FROM idempotency_keys WHERE customer_id = @customerId AND idempotency_key = @key", new { customerId, key }));
        Assert.Equal(0, await CountAsync(fixture.Database,
            "SELECT count(*) FROM outbox_messages WHERE payload->>'sourceWalletId' = @source", new { source = source.ToString() }));

        var retry = await TransferAsync(sender, source, destination, 4_000, key);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(6_000, await GetBalanceAsync(sender, source));
        Assert.Equal(4_000, await GetBalanceAsync(recipient, destination));
        await AssertWalletLedgerConsistentAsync(fixture.Database, source);
        await AssertWalletLedgerConsistentAsync(fixture.Database, destination);
    }

    // DB2/DB3 for credits: the balance never changes without its transaction, entry and audit record.
    [Theory]
    [InlineData(FailurePoint.Audit)]
    [InlineData(FailurePoint.Commit)]
    public async Task Credit_failing_mid_transaction_leaves_no_partial_state_and_can_be_retried(FailurePoint failAt)
    {
        var (owner, walletId) = await CreateFundedWalletAsync(fixture, 0);
        var request = new CreditWalletRequest(9_000, $"NIP-{Guid.NewGuid():N}", null);

        await using (var faulty = FaultyHost(failAt))
        {
            using var faultySettlement = ApiFixture.CreateClientFor(faulty, "svc-nip-settlement", ApiFixture.CreditScope);
            var failed = await faultySettlement.PostAsJsonAsync($"/api/v1/wallets/{walletId}/credits", request);

            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            await AssertNoInternalsLeakedAsync(failed);
        }

        Assert.Equal(0, await GetBalanceAsync(owner, walletId));
        Assert.Equal(0, await CountAsync(fixture.Database,
            "SELECT count(*) FROM ledger_transactions WHERE external_reference = @reference", new { reference = request.ExternalReference }));
        Assert.Equal(0, await CountAsync(fixture.Database, "SELECT count(*) FROM audit_log WHERE wallet_id = @walletId", new { walletId }));

        using var settlement = fixture.CreateSettlementClient();
        var retry = await settlement.PostAsJsonAsync($"/api/v1/wallets/{walletId}/credits", request);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(9_000, await GetBalanceAsync(owner, walletId));
        await AssertWalletLedgerConsistentAsync(fixture.Database, walletId);
    }

    private WebApplicationFactory<Program> FaultyHost(FailurePoint failAt) =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(ILedgerUnitOfWorkFactory));
            services.Remove(real);
            services.AddSingleton<ILedgerUnitOfWorkFactory>(sp => new FaultyUnitOfWorkFactory(
                (ILedgerUnitOfWorkFactory)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!), failAt));
        }));

    private async Task<string> CustomerOfAsync(Guid walletId)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>("SELECT customer_id FROM wallets WHERE id = @walletId", new { walletId })
            ?? throw new InvalidOperationException("Wallet not found.");
    }

    private static async Task AssertNoInternalsLeakedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("internal_error", body);
        Assert.DoesNotContain(SimulatedFailure, body);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain(" at NovaWallet", body);
    }

    private sealed class FaultyUnitOfWorkFactory(ILedgerUnitOfWorkFactory inner, FailurePoint failAt) : ILedgerUnitOfWorkFactory
    {
        public async Task<ILedgerUnitOfWork> BeginAsync(CancellationToken cancellationToken) =>
            new FaultyUnitOfWork(await inner.BeginAsync(cancellationToken), failAt);
    }

    private sealed class FaultyUnitOfWork(ILedgerUnitOfWork inner, FailurePoint failAt) : ILedgerUnitOfWork, IWalletBalances, ILedgerJournal
    {
        public IWalletBalances Wallets => this;

        public IIdempotencyKeys IdempotencyKeys => inner.IdempotencyKeys;

        public IDailyOutboundTotals DailyOutboundTotals => inner.DailyOutboundTotals;

        public ILedgerJournal Journal => this;

        public IOutbox Outbox => inner.Outbox;

        public Task<IReadOnlyList<LockedWallet>> LockPairForUpdateAsync(Guid first, Guid second, CancellationToken cancellationToken) =>
            inner.Wallets.LockPairForUpdateAsync(first, second, cancellationToken);

        public Task<long?> TryDebitAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken) =>
            inner.Wallets.TryDebitAsync(walletId, amountKobo, cancellationToken);

        // In a transfer the source has already been debited when this runs.
        public Task<long?> TryCreditAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken) =>
            failAt == FailurePoint.DestinationCredit
                ? throw new InvalidOperationException(SimulatedFailure)
                : inner.Wallets.TryCreditAsync(walletId, amountKobo, cancellationToken);

        public Task<DateTime> AddTransactionAsync(LedgerTransactionRecord transaction, CancellationToken cancellationToken) =>
            inner.Journal.AddTransactionAsync(transaction, cancellationToken);

        public Task AddEntryAsync(LedgerEntryRecord entry, CancellationToken cancellationToken) =>
            inner.Journal.AddEntryAsync(entry, cancellationToken);

        public Task AddAuditAsync(AuditRecord audit, CancellationToken cancellationToken) =>
            failAt == FailurePoint.Audit
                ? throw new InvalidOperationException(SimulatedFailure)
                : inner.Journal.AddAuditAsync(audit, cancellationToken);

        public Task CommitAsync(CancellationToken cancellationToken) =>
            failAt == FailurePoint.Commit
                ? throw new InvalidOperationException(SimulatedFailure)
                : inner.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
