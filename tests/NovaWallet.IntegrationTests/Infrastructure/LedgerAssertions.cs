using System.Net.Http.Json;
using Dapper;
using NovaWallet.Api.Contracts;

namespace NovaWallet.IntegrationTests.Infrastructure;

public static class LedgerAssertions
{
    private sealed record EntryRow(long EntryNo, Guid TransactionId, string Direction, long AmountKobo, long BalanceAfterKobo);

    private sealed record AuditRow(Guid TransactionId, string Action, long AmountKobo, long BalanceBeforeKobo, long BalanceAfterKobo);

    // Replays the wallet's ledger in order and proves balance, entries and audit trail all agree.
    public static async Task AssertWalletLedgerConsistentAsync(PostgresDatabase database, Guid walletId)
    {
        await using var connection = await database.OpenConnectionAsync();

        var balance = await connection.ExecuteScalarAsync<long>(
            "SELECT balance_kobo FROM wallets WHERE id = @walletId", new { walletId });

        var entries = (await connection.QueryAsync<EntryRow>(
            """
            SELECT entry_no AS EntryNo, transaction_id AS TransactionId, direction AS Direction,
                   amount_kobo AS AmountKobo, balance_after_kobo AS BalanceAfterKobo
            FROM ledger_entries WHERE wallet_id = @walletId ORDER BY entry_no
            """,
            new { walletId })).ToList();

        var audits = (await connection.QueryAsync<AuditRow>(
            """
            SELECT transaction_id AS TransactionId, action AS Action, amount_kobo AS AmountKobo,
                   balance_before_kobo AS BalanceBeforeKobo, balance_after_kobo AS BalanceAfterKobo
            FROM audit_log WHERE wallet_id = @walletId
            """,
            new { walletId })).ToDictionary(a => (a.TransactionId, a.Action));

        long running = 0;
        foreach (var entry in entries)
        {
            var credit = entry.Direction == "CREDIT";
            running += credit ? entry.AmountKobo : -entry.AmountKobo;

            Assert.True(running >= 0, $"Running balance went negative at entry {entry.EntryNo}.");
            Assert.Equal(running, entry.BalanceAfterKobo);

            Assert.True(audits.TryGetValue((entry.TransactionId, credit ? "WALLET_CREDITED" : "WALLET_DEBITED"), out var audit),
                $"Entry {entry.EntryNo} has no matching audit record.");
            Assert.Equal(entry.AmountKobo, audit!.AmountKobo);
            Assert.Equal(entry.BalanceAfterKobo, audit.BalanceAfterKobo);
        }

        Assert.Equal(entries.Count, audits.Count);
        Assert.Equal(running, balance);
    }

    public static async Task<long> GetBalanceAsync(HttpClient ownerClient, Guid walletId)
    {
        var balance = await ownerClient.GetFromJsonAsync<BalanceResponse>($"/api/v1/wallets/{walletId}/balance");
        return balance!.BalanceKobo;
    }

    public static async Task<Guid> CreateWalletAsync(HttpClient ownerClient)
    {
        var response = await ownerClient.PostAsync("/api/v1/wallets", content: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WalletResponse>())!.WalletId;
    }

    public static async Task<(HttpClient Owner, Guid WalletId)> CreateFundedWalletAsync(ApiFixture fixture, long balanceKobo)
    {
        var owner = fixture.CreateClient(ApiFixture.NewCustomerId());
        var walletId = await CreateWalletAsync(owner);

        if (balanceKobo > 0)
        {
            using var settlement = fixture.CreateSettlementClient();
            var response = await settlement.PostAsJsonAsync(
                $"/api/v1/wallets/{walletId}/credits",
                new CreditWalletRequest(balanceKobo, $"NIP-{Guid.NewGuid():N}", null));
            response.EnsureSuccessStatusCode();
        }

        return (owner, walletId);
    }

    public static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, Guid source, Guid destination, long amountKobo, string? idempotencyKey = null, string? narration = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = JsonContent.Create(new TransferRequest(source, destination, amountKobo, "NGN", narration)),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return problem.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    // There is no freeze API, so tests freeze wallets directly in the database.
    public static async Task FreezeWalletAsync(PostgresDatabase database, Guid walletId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE wallets SET status = 'FROZEN' WHERE id = @walletId", new { walletId });
    }

    public static async Task<long> CountAsync(PostgresDatabase database, string sql, object parameters)
    {
        await using var connection = await database.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(sql, parameters);
    }
}
