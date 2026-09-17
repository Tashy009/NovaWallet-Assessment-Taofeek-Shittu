using NovaWallet.Application.Common;

namespace NovaWallet.UnitTests;

public class ValidationErrorsTests
{
    // C2-C5: amounts must be positive and within the transaction cap, including long extremes.
    [Theory]
    [InlineData(1, true)]
    [InlineData(1_000_000_000_000, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(long.MinValue, false)]
    [InlineData(1_000_000_000_001, false)]
    [InlineData(long.MaxValue, false)]
    public void AmountKobo_accepts_only_positive_amounts_within_the_transaction_cap(long amountKobo, bool valid)
    {
        var errors = new ValidationErrors();

        errors.AmountKobo("amountKobo", amountKobo);

        Assert.Equal(valid, !errors.HasErrors);
    }

    // Input: external references reject missing or unsafe values.
    [Theory]
    [InlineData("NIP-000123:abc_1.2", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("has space", false)]
    [InlineData("drop;table", false)]
    public void Required_reference_rejects_missing_or_unsafe_values(string? reference, bool valid)
    {
        var errors = new ValidationErrors();

        errors.Reference("externalReference", reference, required: true);

        Assert.Equal(valid, !errors.HasErrors);
    }

    // Oversized input: references longer than 64 characters are rejected.
    [Fact]
    public void Reference_longer_than_64_characters_is_rejected()
    {
        var errors = new ValidationErrors();

        errors.Reference("externalReference", new string('a', 65), required: true);

        Assert.True(errors.HasErrors);
    }

    // Input: narrations reject control characters.
    [Theory]
    [InlineData(null, true)]
    [InlineData("School fees", true)]
    [InlineData("line\nbreak", false)]
    public void Narration_rejects_control_characters(string? narration, bool valid)
    {
        var errors = new ValidationErrors();

        errors.Narration("narration", narration);

        Assert.Equal(valid, !errors.HasErrors);
    }

    // Errors: validation messages are grouped by field.
    [Fact]
    public void ToError_groups_messages_by_field()
    {
        var errors = new ValidationErrors();
        errors.AmountKobo("amountKobo", 0);
        errors.Reference("externalReference", null, required: true);

        var error = errors.ToError();

        Assert.Equal(ErrorKind.Validation, error.Kind);
        Assert.Equal(["amountKobo", "externalReference"], error.ValidationErrors!.Keys.Order());
    }
}
