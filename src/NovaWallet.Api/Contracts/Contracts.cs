namespace NovaWallet.Api.Contracts;

public sealed record WalletResponse(
    Guid WalletId,
    string CustomerId,
    string Currency,
    long BalanceKobo,
    long DailyOutboundLimitKobo,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record BalanceResponse(Guid WalletId, string Currency, long BalanceKobo);

public sealed record CreditWalletRequest(long AmountKobo, string? ExternalReference, string? Narration);

public sealed record CreditResponse(
    Guid TransactionId,
    Guid WalletId,
    string Currency,
    long AmountKobo,
    long BalanceAfterKobo,
    string ExternalReference,
    string? Narration,
    DateTimeOffset CreatedAt);

public sealed record TransferRequest(
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    string? Currency,
    string? Narration);

public sealed record TransferResponse(
    Guid TransactionId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    string Currency,
    long AmountKobo,
    long SourceBalanceAfterKobo,
    string? Narration,
    DateOnly BusinessDate,
    DateTimeOffset CreatedAt);

public sealed record StatementItemResponse(
    Guid TransactionId,
    string Type,
    string Direction,
    string Currency,
    long AmountKobo,
    long BalanceAfterKobo,
    Guid? CounterpartyWalletId,
    string? ExternalReference,
    string? Narration,
    DateTimeOffset CreatedAt);

public sealed record StatementResponse(Guid WalletId, IReadOnlyList<StatementItemResponse> Items, string? NextCursor);

public sealed record DevTokenRequest(string? CustomerId, string[]? Scopes);

public sealed record DevTokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);
