using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NovaWallet.Infrastructure.Migrations;
using NovaWallet.IntegrationTests.Infrastructure;

namespace NovaWallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SchemaInvariantTests(ApiFixture fixture)
{
    // Deployment: migrations are applied once and rerunning them is a no-op.
    [Fact]
    public async Task Migrations_are_applied_once_and_rerunning_is_a_no_op()
    {
        var migrator = fixture.Factory.Services.GetRequiredService<DatabaseMigrator>();

        await migrator.MigrateAsync(CancellationToken.None);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        var applied = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM schemaversions WHERE scriptname LIKE '%0001_initial_schema.sql'");
        Assert.Equal(1, applied);
    }

    // DB invariant: balance >= 0 is enforced by the database.
    [Fact]
    public async Task Wallet_balance_cannot_go_negative()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var walletId = await InsertWalletAsync(connection, balanceKobo: 100);

        await AssertRejectedAsync(PostgresErrorCodes.CheckViolation, "ck_wallets_balance_non_negative", () =>
            connection.ExecuteAsync("UPDATE wallets SET balance_kobo = balance_kobo - 101 WHERE id = @walletId", new { walletId }));

        Assert.Equal(100, await connection.ExecuteScalarAsync<long>("SELECT balance_kobo FROM wallets WHERE id = @walletId", new { walletId }));
    }

    // DB invariant: wallet currency must be NGN.
    [Fact]
    public async Task Wallet_currency_must_be_ngn()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();

        await AssertRejectedAsync(PostgresErrorCodes.CheckViolation, "ck_wallets_currency_ngn", () =>
            connection.ExecuteAsync(
                "INSERT INTO wallets (id, customer_id, currency) VALUES (@id, @customerId, 'USD')",
                new { id = Guid.NewGuid(), customerId = NewCustomerId() }));
    }

    // DB invariant: one NGN wallet per customer.
    [Fact]
    public async Task Customer_cannot_have_two_ngn_wallets()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var customerId = NewCustomerId();
        await InsertWalletAsync(connection, customerId: customerId);

        await AssertRejectedAsync(PostgresErrorCodes.UniqueViolation, "uq_wallets_customer_currency", () =>
            InsertWalletAsync(connection, customerId: customerId));
    }

    // DB invariant: transfer source and destination must differ.
    [Fact]
    public async Task Transfer_to_the_same_wallet_is_rejected()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var walletId = await InsertWalletAsync(connection);

        await AssertRejectedAsync(PostgresErrorCodes.CheckViolation, "ck_ledger_transactions_shape", () =>
            InsertTransactionAsync(connection, "TRANSFER", 100, sourceWalletId: walletId, destinationWalletId: walletId));
    }

    // DB invariant: a credit has no source wallet.
    [Fact]
    public async Task Credit_with_a_source_wallet_is_rejected()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var source = await InsertWalletAsync(connection);
        var destination = await InsertWalletAsync(connection);

        await AssertRejectedAsync(PostgresErrorCodes.CheckViolation, "ck_ledger_transactions_shape", () =>
            InsertTransactionAsync(connection, "CREDIT", 100, sourceWalletId: source, destinationWalletId: destination));
    }

    // DB invariant: transaction amounts must be positive.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Transaction_amount_must_be_positive(long amountKobo)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var walletId = await InsertWalletAsync(connection);

        await AssertRejectedAsync(PostgresErrorCodes.CheckViolation, "ck_ledger_transactions_amount_positive", () =>
            InsertTransactionAsync(connection, "CREDIT", amountKobo, sourceWalletId: null, destinationWalletId: walletId));
    }

    // DB invariant: an external reference can be posted only once.
    [Fact]
    public async Task Same_external_reference_cannot_be_credited_twice()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var walletId = await InsertWalletAsync(connection);
        var reference = $"NIP-{Guid.NewGuid():N}";

        await InsertTransactionAsync(connection, "CREDIT", 100, null, walletId, externalReference: reference);

        await AssertRejectedAsync(PostgresErrorCodes.UniqueViolation, "ux_ledger_transactions_external_reference", () =>
            InsertTransactionAsync(connection, "CREDIT", 100, null, walletId, externalReference: reference));
    }

    // DB invariant: the daily outbound total cannot be negative.
    [Fact]
    public async Task Daily_outbound_total_cannot_be_negative()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var walletId = await InsertWalletAsync(connection);

        await AssertRejectedAsync(PostgresErrorCodes.CheckViolation, "ck_daily_outbound_totals_non_negative", () =>
            connection.ExecuteAsync(
                "INSERT INTO daily_outbound_totals (wallet_id, business_date, total_kobo) VALUES (@walletId, CURRENT_DATE, -1)",
                new { walletId }));
    }

    // A7: audit rows must satisfy after = before +/- amount.
    [Fact]
    public async Task Audit_row_must_be_arithmetically_consistent()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var walletId = await InsertWalletAsync(connection);
        var transactionId = await InsertTransactionAsync(connection, "CREDIT", 100, null, walletId);

        await AssertRejectedAsync(PostgresErrorCodes.CheckViolation, "ck_audit_log_arithmetic", () =>
            InsertAuditAsync(connection, transactionId, walletId, "WALLET_CREDITED", amountKobo: 100, before: 0, after: 150));
    }

    // A4/A5: UPDATE and DELETE on audit, entries and transactions are blocked.
    [Theory]
    [InlineData("UPDATE audit_log SET amount_kobo = amount_kobo + 1 WHERE transaction_id = @transactionId")]
    [InlineData("DELETE FROM audit_log WHERE transaction_id = @transactionId")]
    [InlineData("UPDATE ledger_entries SET amount_kobo = amount_kobo + 1 WHERE transaction_id = @transactionId")]
    [InlineData("DELETE FROM ledger_entries WHERE transaction_id = @transactionId")]
    [InlineData("UPDATE ledger_transactions SET amount_kobo = amount_kobo + 1 WHERE id = @transactionId")]
    [InlineData("DELETE FROM ledger_transactions WHERE id = @transactionId")]
    public async Task Posted_financial_records_are_append_only(string tamperSql)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var walletId = await InsertWalletAsync(connection, balanceKobo: 100);
        var transactionId = await InsertTransactionAsync(connection, "CREDIT", 100, null, walletId);
        await connection.ExecuteAsync(
            """
            INSERT INTO ledger_entries (transaction_id, wallet_id, direction, amount_kobo, balance_after_kobo)
            VALUES (@transactionId, @walletId, 'CREDIT', 100, 100)
            """,
            new { transactionId, walletId });
        await InsertAuditAsync(connection, transactionId, walletId, "WALLET_CREDITED", amountKobo: 100, before: 0, after: 100);

        var ex = await AssertRejectedAsync(PostgresErrorCodes.RestrictViolation, constraint: null, () =>
            connection.ExecuteAsync(tamperSql, new { transactionId }));

        Assert.Contains("append-only", ex.MessageText);
    }

    // A6: TRUNCATE on append-only tables is blocked.
    [Theory]
    [InlineData("audit_log")]
    [InlineData("ledger_entries")]
    [InlineData("ledger_transactions")]
    public async Task Append_only_tables_cannot_be_truncated(string table)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await AssertRejectedAsync(PostgresErrorCodes.RestrictViolation, constraint: null, () =>
            connection.ExecuteAsync($"TRUNCATE {table} CASCADE", transaction: transaction));
    }

    // DB invariant: a transfer must have a source wallet.
    [Fact]
    public async Task Transfer_without_a_source_wallet_is_rejected()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var walletId = await InsertWalletAsync(connection);

        await AssertRejectedAsync(PostgresErrorCodes.CheckViolation, "ck_ledger_transactions_shape", () =>
            InsertTransactionAsync(connection, "TRANSFER", 100, sourceWalletId: null, destinationWalletId: walletId));
    }

    // DB invariant: an idempotency key can be stored only once per customer and operation.
    [Fact]
    public async Task Same_idempotency_key_cannot_be_stored_twice()
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var parameters = new { customerId = NewCustomerId(), key = Guid.NewGuid().ToString("N"), hash = new string('a', 64) };
        const string sql = """
            INSERT INTO idempotency_keys (customer_id, operation, idempotency_key, request_hash)
            VALUES (@customerId, 'TRANSFER', @key, @hash)
            """;
        await connection.ExecuteAsync(sql, parameters);

        await AssertRejectedAsync(PostgresErrorCodes.UniqueViolation, "pk_idempotency_keys", () => connection.ExecuteAsync(sql, parameters));
    }

    private static string NewCustomerId() => $"cust-{Guid.NewGuid():N}";

    private static async Task<Guid> InsertWalletAsync(NpgsqlConnection connection, long balanceKobo = 0, string? customerId = null)
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(
            "INSERT INTO wallets (id, customer_id, balance_kobo) VALUES (@id, @customerId, @balanceKobo)",
            new { id, customerId = customerId ?? NewCustomerId(), balanceKobo });
        return id;
    }

    private static async Task<Guid> InsertTransactionAsync(
        NpgsqlConnection connection, string type, long amountKobo, Guid? sourceWalletId, Guid destinationWalletId,
        string? externalReference = null)
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO ledger_transactions
                (id, type, amount_kobo, source_wallet_id, destination_wallet_id, business_date,
                 external_reference, initiated_by, correlation_id)
            VALUES
                (@id, @type, @amountKobo, @sourceWalletId, @destinationWalletId, CURRENT_DATE,
                 @externalReference, 'test', 'test')
            """,
            new { id, type, amountKobo, sourceWalletId, destinationWalletId, externalReference });
        return id;
    }

    private static Task InsertAuditAsync(
        NpgsqlConnection connection, Guid transactionId, Guid walletId, string action, long amountKobo, long before, long after) =>
        connection.ExecuteAsync(
            """
            INSERT INTO audit_log
                (event_id, transaction_id, wallet_id, action, amount_kobo, balance_before_kobo, balance_after_kobo, actor_id, correlation_id)
            VALUES
                (@eventId, @transactionId, @walletId, @action, @amountKobo, @before, @after, 'test', 'test')
            """,
            new { eventId = Guid.NewGuid(), transactionId, walletId, action, amountKobo, before, after });

    private static async Task<PostgresException> AssertRejectedAsync(string sqlState, string? constraint, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(sqlState, ex.SqlState);
        if (constraint is not null)
        {
            Assert.Equal(constraint, ex.ConstraintName);
        }

        return ex;
    }
}
