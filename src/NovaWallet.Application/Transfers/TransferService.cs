using System.Text.Json;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;
using NovaWallet.Application.Wallets;
using NovaWallet.Domain;

namespace NovaWallet.Application.Transfers;

public sealed record TransferCommand(
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    string? Currency,
    string? Narration);

public sealed record TransferReceipt(
    Guid TransactionId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    string Currency,
    long AmountKobo,
    long SourceBalanceAfterKobo,
    string? Narration,
    DateOnly BusinessDate,
    DateTimeOffset CreatedAt);

// Exactly one of Receipt or Rejection is set. Rejections are business outcomes stored against the idempotency key.
public sealed record TransferOutcome(TransferReceipt? Receipt, AppError? Rejection, bool Replayed);

public sealed class TransferService(ILedgerUnitOfWorkFactory unitOfWork, TimeProvider timeProvider)
{
    public const string Operation = "TRANSFER";
    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<TransferOutcome>> TransferAsync(
        Caller caller, TransferCommand command, string? idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var validation = new ValidationErrors();
        validation.IdempotencyKey("Idempotency-Key", idempotencyKey);
        if (command.SourceWalletId == Guid.Empty)
        {
            validation.Add("sourceWalletId", "Value is required.");
        }

        if (command.DestinationWalletId == Guid.Empty)
        {
            validation.Add("destinationWalletId", "Value is required.");
        }
        else if (command.DestinationWalletId == command.SourceWalletId)
        {
            validation.Add("destinationWalletId", "Source and destination wallets must be different.");
        }

        validation.AmountKobo("amountKobo", command.AmountKobo);
        if (command.Currency is not null && command.Currency != Currency.Ngn)
        {
            validation.Add("currency", "Only NGN is supported.");
        }

        validation.Narration("narration", command.Narration);
        if (validation.HasErrors)
        {
            return validation.ToError();
        }

        var requestHash = RequestHasher.Transfer(
            command.SourceWalletId, command.DestinationWalletId, command.AmountKobo, Currency.Ngn, command.Narration);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteAsync(caller, command, idempotencyKey!, requestHash, correlationId, cancellationToken);
            }
            catch (TransientConcurrencyException) when (attempt < MaxAttempts)
            {
                // Nothing was committed; back off with jitter and run the whole transaction again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), cancellationToken);
            }
        }
    }

    private async Task<Result<TransferOutcome>> ExecuteAsync(
        Caller caller, TransferCommand command, string idempotencyKey, string requestHash, string correlationId,
        CancellationToken cancellationToken)
    {
        await using var uow = await unitOfWork.BeginAsync(cancellationToken);

        var previous = await uow.ClaimIdempotencyKeyAsync(caller.CustomerId, Operation, idempotencyKey, requestHash, cancellationToken);
        if (previous is not null)
        {
            return previous.RequestHash == requestHash
                ? Replay(previous)
                : Errors.IdempotencyKeyReused();
        }

        var locked = await uow.LockWalletsAsync(command.SourceWalletId, command.DestinationWalletId, cancellationToken);
        var source = locked.SingleOrDefault(w => w.Id == command.SourceWalletId);
        var destination = locked.SingleOrDefault(w => w.Id == command.DestinationWalletId);

        if (source is null || source.CustomerId != caller.CustomerId)
        {
            return await RejectAsync(uow, caller, idempotencyKey, Errors.SourceWalletNotFound(), cancellationToken);
        }

        if (destination is null)
        {
            return await RejectAsync(uow, caller, idempotencyKey, Errors.DestinationWalletNotFound(), cancellationToken);
        }

        if (source.Status != WalletStatuses.Active || destination.Status != WalletStatuses.Active)
        {
            return await RejectAsync(uow, caller, idempotencyKey, Errors.WalletNotActive(), cancellationToken);
        }

        var businessDate = BusinessDay.For(timeProvider.GetUtcNow());
        var spentToday = await uow.GetDailyOutboundTotalAsync(source.Id, businessDate, cancellationToken);

        switch (TransferPolicy.Evaluate(source.BalanceKobo, spentToday, command.AmountKobo, source.DailyOutboundLimitKobo))
        {
            case TransferDecision.InsufficientFunds:
                return await RejectAsync(uow, caller, idempotencyKey, Errors.InsufficientFunds(), cancellationToken);
            case TransferDecision.DailyLimitExceeded:
                var remaining = Math.Max(0, source.DailyOutboundLimitKobo - spentToday);
                return await RejectAsync(uow, caller, idempotencyKey,
                    Errors.DailyLimitExceeded(source.DailyOutboundLimitKobo, remaining), cancellationToken);
        }

        var sourceAfter = await uow.DebitWalletAsync(source.Id, command.AmountKobo, cancellationToken)
            ?? throw new LedgerInvariantViolationException("Conditional debit affected no rows after checks passed under lock.");

        var destinationAfter = await uow.CreditWalletAsync(destination.Id, command.AmountKobo, cancellationToken)
            ?? throw new LedgerInvariantViolationException("Destination credit affected no rows after checks passed under lock.");

        _ = await uow.AddDailyOutboundAsync(source.Id, businessDate, command.AmountKobo, source.DailyOutboundLimitKobo, cancellationToken)
            ?? throw new LedgerInvariantViolationException("Daily limit upsert affected no rows after checks passed under lock.");

        var transactionId = Guid.NewGuid();
        var createdAt = await uow.InsertTransactionAsync(
            new LedgerTransactionRecord(transactionId, TransactionTypes.Transfer, command.AmountKobo, source.Id, destination.Id,
                businessDate, ExternalReference: null, command.Narration, caller.CustomerId, correlationId),
            cancellationToken);

        await uow.InsertEntryAsync(
            new LedgerEntryRecord(transactionId, source.Id, EntryDirections.Debit, command.AmountKobo, sourceAfter), cancellationToken);
        await uow.InsertEntryAsync(
            new LedgerEntryRecord(transactionId, destination.Id, EntryDirections.Credit, command.AmountKobo, destinationAfter), cancellationToken);

        await uow.InsertAuditAsync(
            new AuditRecord(Guid.NewGuid(), transactionId, source.Id, AuditActions.WalletDebited, command.AmountKobo,
                sourceAfter + command.AmountKobo, sourceAfter, caller.CustomerId, correlationId),
            cancellationToken);
        await uow.InsertAuditAsync(
            new AuditRecord(Guid.NewGuid(), transactionId, destination.Id, AuditActions.WalletCredited, command.AmountKobo,
                destinationAfter - command.AmountKobo, destinationAfter, caller.CustomerId, correlationId),
            cancellationToken);

        // Only the sender's balance is returned; the recipient's balance is not the sender's data.
        var receipt = new TransferReceipt(transactionId, source.Id, destination.Id, Currency.Ngn, command.AmountKobo,
            sourceAfter, command.Narration, businessDate, WalletService.Utc(createdAt));

        await uow.CompleteIdempotencyKeyAsync(caller.CustomerId, Operation, idempotencyKey, 201,
            JsonSerializer.Serialize(new StoredOutcome(receipt, null), Json), transactionId, cancellationToken);

        await uow.CommitAsync(cancellationToken);
        return new TransferOutcome(receipt, null, Replayed: false);
    }

    // Nothing has been mutated at this point; only the outcome is persisted against the key.
    private static async Task<Result<TransferOutcome>> RejectAsync(
        ILedgerUnitOfWork uow, Caller caller, string idempotencyKey, AppError rejection, CancellationToken cancellationToken)
    {
        var stored = new StoredOutcome(null, new StoredRejection(rejection.Kind, rejection.Code, rejection.Message, rejection.Details));

        await uow.CompleteIdempotencyKeyAsync(caller.CustomerId, Operation, idempotencyKey, Errors.StatusCode(rejection.Kind),
            JsonSerializer.Serialize(stored, Json), transactionId: null, cancellationToken);

        await uow.CommitAsync(cancellationToken);
        return new TransferOutcome(null, rejection, Replayed: false);
    }

    private static TransferOutcome Replay(StoredIdempotencyResult previous)
    {
        var stored = JsonSerializer.Deserialize<StoredOutcome>(previous.ResponseBody, Json)
            ?? throw new LedgerInvariantViolationException("Stored idempotency outcome is empty.");

        if (stored.Receipt is not null)
        {
            return new TransferOutcome(stored.Receipt, null, Replayed: true);
        }

        var r = stored.Rejection ?? throw new LedgerInvariantViolationException("Stored idempotency outcome has no result.");
        return new TransferOutcome(null, new AppError(r.Kind, r.Code, r.Message) { Details = r.Details }, Replayed: true);
    }

    private sealed record StoredOutcome(TransferReceipt? Receipt, StoredRejection? Rejection);

    private sealed record StoredRejection(ErrorKind Kind, string Code, string Message, IReadOnlyDictionary<string, object?>? Details);
}
