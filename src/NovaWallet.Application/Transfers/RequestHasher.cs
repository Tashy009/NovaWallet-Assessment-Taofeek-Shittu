using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Application.Transfers;

public static class RequestHasher
{
    // Hashes a canonical form, not raw JSON, so whitespace or property order never counts as a different payload.
    public static string Transfer(Guid sourceWalletId, Guid destinationWalletId, long amountKobo, string currency, string? narration)
    {
        var canonical = string.Join('\n',
            "TRANSFER",
            sourceWalletId.ToString("D"),
            destinationWalletId.ToString("D"),
            amountKobo.ToString(CultureInfo.InvariantCulture),
            currency,
            narration ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
