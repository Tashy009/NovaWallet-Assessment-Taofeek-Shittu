using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;
using NovaWallet.Domain;

namespace NovaWallet.Application.Credits;

public sealed class CreditService(
    IWalletStore wallets,
    ILedgerReader ledger,
    ILedgerUnitOfWorkFactory unitOfWork,
    IConcurrencyRetryPolicy retryPolicy,
    TimeProvider timeProvider) : ICreditService
{
    public async Task<Result<CreditOutcome>> CreditAsync(
        Caller caller, CreditWalletCommand command, string correlationId, CancellationToken cancellationToken)
    {
        if (!caller.HasScope(LedgerScopes.PostCredits))
        {
            return Errors.MissingScope(LedgerScopes.PostCredits);
        }

        if (CreditCommandValidator.Validate(command) is { } validationError)
        {
            return validationError;
        }

        try
        {
            return await retryPolicy.ExecuteAsync(ct => PostAsync(caller, command, correlationId, ct), cancellationToken);
        }
        catch (DuplicateExternalReferenceException)
        {
            // The whole transaction (including the balance increment) was rolled back; report the original posting.
            return await ReplayAsync(command, cancellationToken);
        }
    }

    private async Task<Result<CreditOutcome>> PostAsync(
        Caller caller, CreditWalletCommand command, string correlationId, CancellationToken cancellationToken)
    {
        await using var uow = await unitOfWork.BeginAsync(cancellationToken);

        var balanceAfter = await uow.Wallets.TryCreditAsync(command.WalletId, command.AmountKobo, cancellationToken);
        if (balanceAfter is null)
        {
            return await wallets.FindAsync(command.WalletId, cancellationToken) is null
                ? Errors.WalletNotFound()
                : Errors.WalletNotActive();
        }

        var transactionId = Guid.NewGuid();
        var createdAt = await uow.Journal.AddTransactionAsync(
            new LedgerTransactionRecord(transactionId, TransactionTypes.Credit, command.AmountKobo, SourceWalletId: null,
                command.WalletId, BusinessDay.For(timeProvider.GetUtcNow()), command.ExternalReference, command.Narration,
                caller.CustomerId, correlationId),
            cancellationToken);

        await uow.Journal.AddEntryAsync(
            new LedgerEntryRecord(transactionId, command.WalletId, EntryDirections.Credit, command.AmountKobo, balanceAfter.Value),
            cancellationToken);

        await uow.Journal.AddAuditAsync(
            new AuditRecord(Guid.NewGuid(), transactionId, command.WalletId, AuditActions.WalletCredited, command.AmountKobo,
                balanceAfter.Value - command.AmountKobo, balanceAfter.Value, caller.CustomerId, correlationId),
            cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return new CreditOutcome(
            new CreditReceipt(transactionId, command.WalletId, Currency.Ngn, command.AmountKobo, balanceAfter.Value,
                command.ExternalReference!, command.Narration, UtcTime.From(createdAt)),
            Replayed: false);
    }

    private async Task<Result<CreditOutcome>> ReplayAsync(CreditWalletCommand command, CancellationToken cancellationToken)
    {
        var original = await ledger.FindCreditByReferenceAsync(command.ExternalReference!, cancellationToken)
            ?? throw new LedgerInvariantViolationException("Duplicate external reference reported but no committed credit exists.");

        var samePayload = original.WalletId == command.WalletId
            && original.AmountKobo == command.AmountKobo
            && string.Equals(original.Narration, command.Narration, StringComparison.Ordinal);

        if (!samePayload)
        {
            return Errors.ExternalReferenceConflict();
        }

        return new CreditOutcome(
            new CreditReceipt(original.TransactionId, original.WalletId, Currency.Ngn, original.AmountKobo,
                original.BalanceAfterKobo, original.ExternalReference, original.Narration, UtcTime.From(original.CreatedAt)),
            Replayed: true);
    }
}
