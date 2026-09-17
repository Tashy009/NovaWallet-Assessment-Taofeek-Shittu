namespace NovaWallet.Application.Common;

public static class Errors
{
    // Stored idempotent outcomes replay the original status, so the mapping lives next to the error catalogue.
    public static int StatusCode(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => 400,
        ErrorKind.Forbidden => 403,
        ErrorKind.NotFound => 404,
        ErrorKind.Conflict => 409,
        ErrorKind.BusinessRule => 422,
        _ => 500,
    };

    public static AppError WalletNotFound() =>
        new(ErrorKind.NotFound, "wallet_not_found", "Wallet was not found.");

    public static AppError SourceWalletNotFound() =>
        new(ErrorKind.NotFound, "source_wallet_not_found", "Source wallet was not found.");

    public static AppError DestinationWalletNotFound() =>
        new(ErrorKind.NotFound, "destination_wallet_not_found", "Destination wallet was not found.");

    public static AppError WalletNotActive() =>
        new(ErrorKind.BusinessRule, "wallet_not_active", "Wallet is not active.");

    public static AppError WalletAlreadyExists(Guid existingWalletId) =>
        new(ErrorKind.Conflict, "wallet_already_exists", "Customer already has an NGN wallet.")
        {
            Details = new Dictionary<string, object?> { ["walletId"] = existingWalletId },
        };

    public static AppError ExternalReferenceConflict() =>
        new(ErrorKind.Conflict, "external_reference_conflict",
            "This external reference was already used for a different credit.");

    public static AppError InsufficientFunds() =>
        new(ErrorKind.BusinessRule, "insufficient_funds", "Source wallet balance is insufficient for this transfer.");

    public static AppError DailyLimitExceeded(long dailyLimitKobo, long remainingTodayKobo) =>
        new(ErrorKind.BusinessRule, "daily_limit_exceeded", "Transfer would exceed the daily outbound limit.")
        {
            Details = new Dictionary<string, object?>
            {
                ["dailyLimitKobo"] = dailyLimitKobo,
                ["remainingTodayKobo"] = remainingTodayKobo,
            },
        };

    public static AppError IdempotencyKeyReused() =>
        new(ErrorKind.BusinessRule, "idempotency_key_reused",
            "This Idempotency-Key was already used with a different request payload.");

    public static AppError MissingScope(string scope) =>
        new(ErrorKind.Forbidden, "insufficient_scope", $"The '{scope}' scope is required.");
}
