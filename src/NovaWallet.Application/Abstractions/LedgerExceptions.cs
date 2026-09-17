namespace NovaWallet.Application.Abstractions;

public sealed class DuplicateExternalReferenceException(string externalReference)
    : Exception($"External reference '{externalReference}' has already been posted.");

// Deadlock or serialization failure: the transaction was rolled back and is safe to retry.
public sealed class TransientConcurrencyException(Exception inner)
    : Exception("The database aborted the transaction due to a concurrency conflict.", inner);

// A guarded statement affected no rows after the checks passed under lock; never expected, always rolled back.
public sealed class LedgerInvariantViolationException(string message) : Exception(message);
