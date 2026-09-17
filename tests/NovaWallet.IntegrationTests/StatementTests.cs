using System.Net;
using System.Net.Http.Json;
using NovaWallet.Api.Contracts;
using NovaWallet.IntegrationTests.Infrastructure;
using static NovaWallet.IntegrationTests.Infrastructure.LedgerAssertions;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class StatementTests(ApiFixture fixture)
{
    [Fact]
    public async Task Statement_lists_credits_and_transfers_newest_first_with_counterparties()
    {
        var (sender, source) = await CreateFundedWalletAsync(fixture, 100_000);
        var (recipient, destination) = await CreateFundedWalletAsync(fixture, 0);
        await TransferAsync(sender, source, destination, 30_000, narration: "Rent");

        var senderPage = await sender.GetFromJsonAsync<StatementResponse>($"/api/v1/wallets/{source}/transactions");
        var recipientPage = await recipient.GetFromJsonAsync<StatementResponse>($"/api/v1/wallets/{destination}/transactions");

        Assert.Collection(senderPage!.Items,
            debit =>
            {
                Assert.Equal(("TRANSFER", "DEBIT", 30_000L, 70_000L), (debit.Type, debit.Direction, debit.AmountKobo, debit.BalanceAfterKobo));
                Assert.Equal(destination, debit.CounterpartyWalletId);
                Assert.Equal("Rent", debit.Narration);
            },
            credit =>
            {
                Assert.Equal(("CREDIT", "CREDIT", 100_000L, 100_000L), (credit.Type, credit.Direction, credit.AmountKobo, credit.BalanceAfterKobo));
                Assert.Null(credit.CounterpartyWalletId);
                Assert.StartsWith("NIP-", credit.ExternalReference);
            });
        Assert.Null(senderPage.NextCursor);

        var received = Assert.Single(recipientPage!.Items);
        Assert.Equal(("CREDIT", 30_000L, source), (received.Direction, received.AmountKobo, received.CounterpartyWalletId));
    }

    [Fact]
    public async Task Pages_cover_every_entry_exactly_once_in_descending_order()
    {
        var (owner, walletId) = await CreateFundedWalletAsync(fixture, 0);
        using var settlement = fixture.CreateSettlementClient();
        for (var i = 1; i <= 25; i++)
        {
            (await settlement.PostAsJsonAsync($"/api/v1/wallets/{walletId}/credits",
                new CreditWalletRequest(i, $"NIP-{Guid.NewGuid():N}", null))).EnsureSuccessStatusCode();
        }

        var pages = new List<StatementResponse>();
        string? cursor = null;
        do
        {
            var url = $"/api/v1/wallets/{walletId}/transactions?limit=10" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = await owner.GetFromJsonAsync<StatementResponse>(url);
            pages.Add(page!);
            cursor = page!.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal([10, 10, 5], pages.Select(p => p.Items.Count));
        var amounts = pages.SelectMany(p => p.Items).Select(i => i.AmountKobo).ToList();
        Assert.Equal(Enumerable.Range(1, 25).Reverse().Select(i => (long)i), amounts);
    }

    [Fact]
    public async Task New_entries_arriving_between_pages_do_not_shift_later_pages()
    {
        var (owner, walletId) = await CreateFundedWalletAsync(fixture, 0);
        using var settlement = fixture.CreateSettlementClient();
        for (var i = 1; i <= 6; i++)
        {
            await settlement.PostAsJsonAsync($"/api/v1/wallets/{walletId}/credits", new CreditWalletRequest(i, $"NIP-{Guid.NewGuid():N}", null));
        }

        var first = await owner.GetFromJsonAsync<StatementResponse>($"/api/v1/wallets/{walletId}/transactions?limit=3");
        await settlement.PostAsJsonAsync($"/api/v1/wallets/{walletId}/credits", new CreditWalletRequest(999, $"NIP-{Guid.NewGuid():N}", null));
        var second = await owner.GetFromJsonAsync<StatementResponse>($"/api/v1/wallets/{walletId}/transactions?limit=3&cursor={first!.NextCursor}");

        Assert.Equal([6L, 5L, 4L], first.Items.Select(i => i.AmountKobo));
        Assert.Equal([3L, 2L, 1L], second!.Items.Select(i => i.AmountKobo));
    }

    [Fact]
    public async Task Empty_wallet_returns_empty_page()
    {
        var (owner, walletId) = await CreateFundedWalletAsync(fixture, 0);

        var page = await owner.GetFromJsonAsync<StatementResponse>($"/api/v1/wallets/{walletId}/transactions");

        Assert.Empty(page!.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Another_customers_statement_is_not_found()
    {
        var (_, walletId) = await CreateFundedWalletAsync(fixture, 5_000);
        using var intruder = fixture.CreateClient(ApiFixture.NewCustomerId());

        var response = await intruder.GetAsync($"/api/v1/wallets/{walletId}/transactions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?cursor=garbage")]
    public async Task Invalid_paging_parameters_return_400(string query)
    {
        var (owner, walletId) = await CreateFundedWalletAsync(fixture, 0);

        var response = await owner.GetAsync($"/api/v1/wallets/{walletId}/transactions{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
