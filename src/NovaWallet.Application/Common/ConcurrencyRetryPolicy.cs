using NovaWallet.Application.Abstractions;

namespace NovaWallet.Application.Common;

public interface IConcurrencyRetryPolicy
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

// Re-runs a whole transaction after a deadlock/serialization abort; safe because nothing from the failed attempt was committed.
public sealed class ConcurrencyRetryPolicy : IConcurrencyRetryPolicy
{
    public const int MaxAttempts = 3;

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (TransientConcurrencyException) when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), cancellationToken);
            }
        }
    }
}
