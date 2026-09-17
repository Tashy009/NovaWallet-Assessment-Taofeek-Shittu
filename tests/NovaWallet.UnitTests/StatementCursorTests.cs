using NovaWallet.Application.Statements;

namespace NovaWallet.UnitTests;

public class StatementCursorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(987_654_321)]
    [InlineData(long.MaxValue)]
    public void Encode_then_decode_round_trips(long entryNo)
    {
        var cursor = StatementCursor.Encode(entryNo);

        Assert.True(StatementCursor.TryDecode(cursor, out var decoded));
        Assert.Equal(entryNo, decoded);
        Assert.DoesNotContain('=', cursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("MTIz")]
    [InlineData("djE6LTU")]
    [InlineData("djE6MA")]
    [InlineData("djE6YWJj")]
    public void Malformed_or_tampered_cursors_are_rejected(string? cursor)
    {
        Assert.False(StatementCursor.TryDecode(cursor, out _));
    }
}
