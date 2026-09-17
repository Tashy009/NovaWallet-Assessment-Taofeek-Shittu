using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Common;
using NovaWallet.Application.Wallets;
using NovaWallet.Domain;

namespace NovaWallet.Application.Credits;

public sealed record CreditWalletCommand(Guid WalletId, long AmountKobo, string? ExternalReference, string? Narration);

public sealed record CreditReceipt(
    Guid TransactionId,
    Guid WalletId,
    string Currency,
    long AmountKobo,
    long BalanceAfterKobo,
    string ExternalReference,
    string? Narration,
    DateTimeOffset CreatedAt);

public sealed record CreditOutcome(CreditReceipt Receipt, bool Replayed);

public sealed class CreditService(
    IWalletStore wallets,
    ILedgerReader ledger,
    ILedgerUnitOfWorkFactory unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<CreditOutcome>> CreditAsync(
        Caller caller, CreditWalletCommand command, string correlationId, CancellationToken cancellationToken)
    {
        if (!caller.HasScope(LedgerScopes.PostCredits))
        {
            return Errors.MissingScope(LedgerScopes.PostCredits);
        }

        var validation = new ValidationErrors();
        validation.AmountKobo("amountKobo", command.AmountKobo);
        validation.Reference("externalReference", command.ExternalReference, required: true);
        validation.Narration("narration", command.Narration);
        if (validation.HasErrors)
        {
            return validation.ToError();
        }

        var reference = command.ExternalReference!;

        try
        {
            return await PostAsync(caller, command, reference, correlationId, cancellationToken);
        }
        catch (DuplicateExternalReferenceException)
        {
            // The whole transaction (including the balance increment) was rolled back; report the original posting.
            return await ReplayAsync(command, reference, cancellationToken);
        }
    }

    private async Task<Result<CreditOutcome>> PostAsync(
        Caller caller, CreditWalletCommand command, string reference, string correlationId, CancellationToken cancellationToken)
    {
        await using var uow = await unitOfWork.BeginAsync(cancellationToken);

        var balanceAfter = await uow.CreditWalletAsync(command.WalletId, command.AmountKobo, cancellationToken);
        if (balanceAfter is null)
        {
            return await wallets.FindAsync(command.WalletId, cancellationToken) is null
                ? Errors.WalletNotFound()
                : Errors.WalletNotActive();
        }

        var transactionId = Guid.NewGuid();
        var createdAt = await uow.InsertTransactionAsync(
            new LedgerTransactionRecord(
                transactionId,
                TransactionTypes.Credit,
                command.AmountKobo,
                SourceWalletId: null,
                command.WalletId,
                BusinessDay.For(timeProvider.GetUtcNow()),
                reference,
                command.Narration,
                caller.CustomerId,
                correlationId),
            cancellationToken);

        await uow.InsertEntryAsync(
            new LedgerEntryRecord(transactionId, command.WalletId, EntryDirections.Credit, command.AmountKobo, balanceAfter.Value),
            cancellationToken);

        await uow.InsertAuditAsync(
            new AuditRecord(
                Guid.NewGuid(),
                transactionId,
                command.WalletId,
                AuditActions.WalletCredited,
                command.AmountKobo,
                balanceAfter.Value - command.AmountKobo,
                balanceAfter.Value,
                caller.CustomerId,
                correlationId),
            cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return new CreditOutcome(
            new CreditReceipt(transactionId, command.WalletId, Currency.Ngn, command.AmountKobo, balanceAfter.Value,
                reference, command.Narration, WalletService.Utc(createdAt)),
            Replayed: false);
    }

    private async Task<Result<CreditOutcome>> ReplayAsync(
        CreditWalletCommand command, string reference, CancellationToken cancellationToken)
    {
        var original = await ledger.FindCreditByReferenceAsync(reference, cancellationToken)
            ?? throw new InvalidOperationException("Duplicate external reference reported but no committed credit exists.");

        var samePayload = original.WalletId == command.WalletId
            && original.AmountKobo == command.AmountKobo
            && string.Equals(original.Narration, command.Narration, StringComparison.Ordinal);

        if (!samePayload)
        {
            return Errors.ExternalReferenceConflict();
        }

        return new CreditOutcome(
            new CreditReceipt(original.TransactionId, original.WalletId, Currency.Ngn, original.AmountKobo,
                original.BalanceAfterKobo, original.ExternalReference, original.Narration, WalletService.Utc(original.CreatedAt)),
            Replayed: true);
    }
}
