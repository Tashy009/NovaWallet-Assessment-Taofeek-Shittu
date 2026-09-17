using System.Text.RegularExpressions;
using NovaWallet.Domain;

namespace NovaWallet.Application.Common;

public sealed partial class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            _errors[field] = messages = [];
        }

        messages.Add(message);
    }

    public void AmountKobo(string field, long amountKobo)
    {
        if (amountKobo <= 0)
        {
            Add(field, "Amount must be a positive whole number of kobo.");
        }
        else if (amountKobo > LedgerLimits.MaxTransactionAmountKobo)
        {
            Add(field, $"Amount must not exceed {LedgerLimits.MaxTransactionAmountKobo} kobo.");
        }
    }

    public void Reference(string field, string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                Add(field, "Value is required.");
            }

            return;
        }

        if (value.Length > 64 || !ReferencePattern().IsMatch(value))
        {
            Add(field, "Must be 1-64 characters of letters, digits, '-', '_', '.' or ':'.");
        }
    }

    public void IdempotencyKey(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(field, "The Idempotency-Key header is required.");
        }
        else if (value.Length > 100 || !ReferencePattern().IsMatch(value))
        {
            Add(field, "Must be 1-100 characters of letters, digits, '-', '_', '.' or ':'.");
        }
    }

    public void Narration(string field, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > 140 || value.Any(char.IsControl))
        {
            Add(field, "Must be at most 140 characters with no control characters.");
        }
    }

    public AppError ToError() =>
        new(ErrorKind.Validation, "validation_failed", "One or more validation errors occurred.")
        {
            ValidationErrors = _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal),
        };

    [GeneratedRegex("^[A-Za-z0-9_.:-]+$")]
    private static partial Regex ReferencePattern();
}
