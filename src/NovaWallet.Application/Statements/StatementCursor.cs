using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace NovaWallet.Application.Statements;

public static class StatementCursor
{
    private const string Prefix = "v1:";

    public static string Encode(long entryNo)
    {
        var bytes = Encoding.ASCII.GetBytes(Prefix + entryNo.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? cursor, out long entryNo)
    {
        entryNo = 0;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 64)
        {
            return false;
        }

        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

        var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(base64.Length)];
        if (!Convert.TryFromBase64String(base64, buffer, out var written))
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, written);
        return text.StartsWith(Prefix, StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out entryNo)
            && entryNo > 0;
    }
}
