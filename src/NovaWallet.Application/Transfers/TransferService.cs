using Microsoft.Extensions.Logging;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;
using NovaWallet.Domain;

namespace NovaWallet.Application.Transfers;

public sealed class TransferService(
    ILedgerUnitOfWorkFactory unitOfWork,
    IConcurrencyRetryPolicy retryPolicy,
    TimeProvider timeProvider,
    ILogger<TransferService> logger) : ITransferService
{
    public const string Operation = "TRANSFER";

    public async Task<Result<TransferOutcome>> TransferAsync(
        Caller caller, TransferCommand command, string? idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (TransferCommandValidator.Validate(command, idempotencyKey) is { } validationError)
        {
            return validationError;
        }

        var request = new TransferRequestContext(
            caller,
            command,
            idempotencyKey!,
            RequestHasher.Transfer(command.SourceWalletId, command.DestinationWalletId, command.AmountKobo, Currency.Ngn, command.Narration),
            correlationId);

        return await retryPolicy.ExecuteAsync(ct => ExecuteAsync(request, ct), cancellationToken);
    }

    private async Task<Result<TransferOutcome>> ExecuteAsync(TransferRequestContext request, CancellationToken cancellationToken)
    {
        var (caller, command, idempotencyKey, requestHash, correlationId) = request;
        await using var uow = await unitOfWork.BeginAsync(cancellationToken);

        var previous = await uow.IdempotencyKeys.TryClaimAsync(caller.CustomerId, Operation, idempotencyKey, requestHash, cancellationToken);
        if (previous is not null)
        {
            return previous.RequestHash == requestHash
                ? TransferOutcomeSerializer.DeserializeReplay(previous.ResponseBody)
                : Errors.IdempotencyKeyReused();
        }

        var locked = await uow.Wallets.LockPairForUpdateAsync(command.SourceWalletId, command.DestinationWalletId, cancellationToken);
        var source = locked.SingleOrDefault(w => w.Id == command.SourceWalletId);
        var destination = locked.SingleOrDefault(w => w.Id == command.DestinationWalletId);

        var rejection = CheckWallets(caller, source, destination);
        if (rejection is not null)
        {
            return await RejectAsync(uow, request, rejection, cancellationToken);
        }

        var businessDate = BusinessDay.For(timeProvider.GetUtcNow());
        var spentToday = await uow.DailyOutboundTotals.GetAsync(source!.Id, businessDate, cancellationToken);

        rejection = TransferPolicy.Evaluate(source.BalanceKobo, spentToday, command.AmountKobo, source.DailyOutboundLimitKobo) switch
        {
            TransferDecision.InsufficientFunds => Errors.InsufficientFunds(),
            TransferDecision.DailyLimitExceeded => Errors.DailyLimitExceeded(
                source.DailyOutboundLimitKobo, Math.Max(0, source.DailyOutboundLimitKobo - spentToday)),
            _ => null,
        };

        if (rejection is not null)
        {
            return await RejectAsync(uow, request, rejection, cancellationToken);
        }

        var receipt = await PostAsync(uow, request, source, destination!, businessDate, cancellationToken);
        return new TransferOutcome(receipt, null, Replayed: false);
    }

    private AppError? CheckWallets(Caller caller, LockedWallet? source, LockedWallet? destination)
    {
        if (source is null || source.CustomerId != caller.CustomerId)
        {
            if (source is not null)
            {
                logger.LogWarning("Customer {CustomerId} attempted transfer from wallet {WalletId} owned by another customer",
                    caller.CustomerId, source.Id);
            }

            return Errors.SourceWalletNotFound();
        }

        if (destination is null)
        {
            return Errors.DestinationWalletNotFound();
        }

        return source.Status != WalletStatuses.Active || destination.Status != WalletStatuses.Active
            ? Errors.WalletNotActive()
            : null;
    }

    // All checks have passed under the row locks; every write below is still guarded and aborts on zero rows.
    private static async Task<TransferReceipt> PostAsync(
        ILedgerUnitOfWork uow, TransferRequestContext request, LockedWallet source, LockedWallet destination,
        DateOnly businessDate, CancellationToken cancellationToken)
    {
        var (caller, command, idempotencyKey, _, correlationId) = request;
        var amount = command.AmountKobo;

        var sourceAfter = await uow.Wallets.TryDebitAsync(source.Id, amount, cancellationToken)
            ?? throw new LedgerInvariantViolationException("Conditional debit affected no rows after checks passed under lock.");

        var destinationAfter = await uow.Wallets.TryCreditAsync(destination.Id, amount, cancellationToken)
            ?? throw new LedgerInvariantViolationException("Destination credit affected no rows after checks passed under lock.");

        _ = await uow.DailyOutboundTotals.TryAddAsync(source.Id, businessDate, amount, source.DailyOutboundLimitKobo, cancellationToken)
            ?? throw new LedgerInvariantViolationException("Daily limit upsert affected no rows after checks passed under lock.");

        var transactionId = Guid.NewGuid();
        var createdAt = await uow.Journal.AddTransactionAsync(
            new LedgerTransactionRecord(transactionId, TransactionTypes.Transfer, amount, source.Id, destination.Id,
                businessDate, ExternalReference: null, command.Narration, caller.CustomerId, correlationId),
            cancellationToken);

        await uow.Journal.AddEntryAsync(
            new LedgerEntryRecord(transactionId, source.Id, EntryDirections.Debit, amount, sourceAfter), cancellationToken);
        await uow.Journal.AddEntryAsync(
            new LedgerEntryRecord(transactionId, destination.Id, EntryDirections.Credit, amount, destinationAfter), cancellationToken);

        await uow.Journal.AddAuditAsync(
            new AuditRecord(Guid.NewGuid(), transactionId, source.Id, AuditActions.WalletDebited, amount,
                sourceAfter + amount, sourceAfter, caller.CustomerId, correlationId),
            cancellationToken);
        await uow.Journal.AddAuditAsync(
            new AuditRecord(Guid.NewGuid(), transactionId, destination.Id, AuditActions.WalletCredited, amount,
                destinationAfter - amount, destinationAfter, caller.CustomerId, correlationId),
            cancellationToken);

        // Only the sender's balance is returned; the recipient's balance is not the sender's data.
        var receipt = new TransferReceipt(transactionId, source.Id, destination.Id, Currency.Ngn, amount, sourceAfter,
            command.Narration, businessDate, UtcTime.From(createdAt));

        // Same transaction as the ledger writes: the event exists if and only if the transfer commits.
        await uow.Outbox.AddAsync(
            new TransferCompletedEvent(Guid.NewGuid(), transactionId, source.Id, destination.Id, amount, Currency.Ngn,
                businessDate, receipt.CreatedAt, correlationId).ToOutboxMessage(),
            cancellationToken);

        await uow.IdempotencyKeys.CompleteAsync(caller.CustomerId, Operation, idempotencyKey, 201,
            TransferOutcomeSerializer.Serialize(receipt), transactionId, cancellationToken);

        await uow.CommitAsync(cancellationToken);
        return receipt;
    }

    // Nothing has been mutated at this point; only the outcome is persisted against the key.
    private static async Task<Result<TransferOutcome>> RejectAsync(
        ILedgerUnitOfWork uow, TransferRequestContext request, AppError rejection, CancellationToken cancellationToken)
    {
        await uow.IdempotencyKeys.CompleteAsync(request.Caller.CustomerId, Operation, request.IdempotencyKey,
            Errors.StatusCode(rejection.Kind), TransferOutcomeSerializer.Serialize(rejection), transactionId: null, cancellationToken);

        await uow.CommitAsync(cancellationToken);
        return new TransferOutcome(null, rejection, Replayed: false);
    }

    private sealed record TransferRequestContext(
        Caller Caller, TransferCommand Command, string IdempotencyKey, string RequestHash, string CorrelationId);
}
