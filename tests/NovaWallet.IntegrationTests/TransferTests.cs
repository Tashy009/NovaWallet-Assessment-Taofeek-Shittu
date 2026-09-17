using System.Net;
using System.Net.Http.Json;
using System.Text;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class TransferTests(ApiFixture fixture)
{
    // T1/B3: a transfer debits and credits atomically with two entries and two audit records.
    [Fact]
    public async Task Successful_transfer_moves_funds_atomically_with_entries_and_audit()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 100_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(sender, source, destination, 40_000, narration: "Rent share");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<TransferResponse>();
        Assert.Equal(40_000, receipt!.AmountKobo);
        Assert.Equal(60_000, receipt.SourceBalanceAfterKobo);
        Assert.Equal("Rent share", receipt.Narration);
        Assert.Equal(60_000, await GetBalanceAsync(sender, source));
        Assert.Equal(40_000, await GetBalanceAsync(recipient, destination));

        Assert.Equal(2, await CountAsync(fixture.Database,
            "SELECT count(*) FROM ledger_entries WHERE transaction_id = @id", new { id = receipt.TransactionId }));
        Assert.Equal(2, await CountAsync(fixture.Database,
            "SELECT count(*) FROM audit_log WHERE transaction_id = @id", new { id = receipt.TransactionId }));
        await AssertWalletLedgerConsistentAsync(fixture.Database, source);
        await AssertWalletLedgerConsistentAsync(fixture.Database, destination);
    }

    // T2: transferring the exact balance leaves zero.
    [Fact]
    public async Task Exact_balance_transfer_leaves_zero()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 12_345);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(sender, source, destination, 12_345);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await GetBalanceAsync(sender, source));
    }

    // T3/A3: insufficient funds is rejected with no partial movement and no audit records.
    [Fact]
    public async Task Insufficient_funds_returns_422_and_moves_nothing()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(sender, source, destination, 5_001);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("insufficient_funds", await ErrorCodeAsync(response));
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
        Assert.Equal(0, await GetBalanceAsync(recipient, destination));
        Assert.Equal(0, await CountAsync(fixture.Database,
            "SELECT count(*) FROM ledger_transactions WHERE source_wallet_id = @source", new { source }));
        Assert.Equal(0, await CountAsync(fixture.Database,
            "SELECT count(*) FROM audit_log WHERE (wallet_id = @source AND action = 'WALLET_DEBITED') OR wallet_id = @destination",
            new { source, destination }));
    }

    // T8: an unknown destination returns 404 and moves nothing.
    [Fact]
    public async Task Unknown_destination_returns_404_and_moves_nothing()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);

        var response = await TransferAsync(sender, source, Guid.NewGuid(), 1_000);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("destination_wallet_not_found", await ErrorCodeAsync(response));
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
    }

    // T11/IDOR: a customer cannot transfer from someone else's wallet.
    [Fact]
    public async Task Cannot_transfer_from_a_wallet_the_caller_does_not_own()
    {
        var (victim, victimWallet) = await CreateFundedWalletAsync(fixture, 50_000);
        var (thief, thiefWallet) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(thief, victimWallet, thiefWallet, 50_000);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("source_wallet_not_found", await ErrorCodeAsync(response));
        Assert.Equal(50_000, await GetBalanceAsync(victim, victimWallet));
        Assert.Equal(0, await GetBalanceAsync(thief, thiefWallet));
    }

    // T6: self-transfer is rejected.
    [Fact]
    public async Task Source_equal_to_destination_returns_400()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);

        var response = await TransferAsync(sender, source, source, 1_000);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
    }

    // T4/T5: zero and negative amounts are rejected.
    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public async Task Non_positive_amount_returns_400(long amountKobo)
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(sender, source, destination, amountKobo);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
    }

    // Idempotency: a transfer without an Idempotency-Key is rejected.
    [Fact]
    public async Task Missing_idempotency_key_returns_400()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await sender.PostAsJsonAsync("/api/v1/transfers", new TransferRequest(source, destination, 1_000, "NGN", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
    }

    // T12: a non-NGN currency is rejected.
    [Fact]
    public async Task Non_ngn_currency_returns_400()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (_, destination) = await CreateFundedWalletAsync(fixture, 0);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = new StringContent(
                $$"""{"sourceWalletId":"{{source}}","destinationWalletId":"{{destination}}","amountKobo":1000,"currency":"USD"}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await sender.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // T7: an unknown source wallet is reported as not found.
    [Fact]
    public async Task Unknown_source_returns_404_and_moves_nothing()
    {
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);

        var response = await TransferAsync(recipient, Guid.NewGuid(), destination, 1_000);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("source_wallet_not_found", await ErrorCodeAsync(response));
        Assert.Equal(0, await GetBalanceAsync(recipient, destination));
    }

    // T9/T10: a frozen source or destination blocks the transfer and nothing moves.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Frozen_source_or_destination_returns_422_and_moves_nothing(bool freezeSource)
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 5_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);
        await FreezeWalletAsync(fixture.Database, freezeSource ? source : destination);

        var response = await TransferAsync(sender, source, destination, 1_000);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wallet_not_active", await ErrorCodeAsync(response));
        Assert.Equal(5_000, await GetBalanceAsync(sender, source));
        Assert.Equal(0, await GetBalanceAsync(recipient, destination));
    }
}
