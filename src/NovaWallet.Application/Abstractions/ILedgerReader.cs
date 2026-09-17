namespace NovaWallet.Application.Abstractions;

public interface ILedgerReader
{
    Task<PostedCredit?> FindCreditByReferenceAsync(string externalReference, CancellationToken cancellationToken);
}
