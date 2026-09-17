namespace NovaWallet.Application.Common;

public sealed record Caller(string CustomerId, IReadOnlySet<string> Scopes)
{
    public bool HasScope(string scope) => Scopes.Contains(scope);
}

public static class LedgerScopes
{
    // Held by the internal NIP inbound-settlement service, never by customers.
    public const string PostCredits = "ledger:credit";
}
