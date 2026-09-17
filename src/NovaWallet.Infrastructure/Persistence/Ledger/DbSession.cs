using Dapper;
using Npgsql;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Infrastructure.Persistence.Ledger;

// The connection and transaction shared by every part of one unit of work.
internal sealed class DbSession(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction Transaction { get; } = transaction;

    public CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, Transaction, cancellationToken: cancellationToken);

    public static async Task<T> Guarded<T>(Func<Task<T>> statement)
    {
        try
        {
            return await statement();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
        {
            throw new TransientConcurrencyException(ex);
        }
    }
}
