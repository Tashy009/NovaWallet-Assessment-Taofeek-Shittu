using NovaWallet.Application.Transfers;
using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class TransferPolicyTests
{
    private const long Limit = 50_000_000;

    // Transfer policy: funds are checked before the daily limit, with exact boundaries.
    [Theory]
    [InlineData(10_000, 0, 10_000, TransferDecision.Allowed)]
    [InlineData(10_000, 0, 10_001, TransferDecision.InsufficientFunds)]
    [InlineData(0, 0, 1, TransferDecision.InsufficientFunds)]
    [InlineData(90_000_000, 49_999_999, 1, TransferDecision.Allowed)]
    [InlineData(90_000_000, 50_000_000, 1, TransferDecision.DailyLimitExceeded)]
    [InlineData(90_000_000, 30_000_000, 25_000_000, TransferDecision.DailyLimitExceeded)]
    [InlineData(1_000, 50_000_000, 5_000, TransferDecision.InsufficientFunds)]
    public void Evaluate_applies_funds_then_daily_limit(long balance, long spentToday, long amount, TransferDecision expected)
    {
        Assert.Equal(expected, TransferPolicy.Evaluate(balance, spentToday, amount, Limit));
    }

    // T4/T5: the policy rejects non-positive amounts.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Evaluate_rejects_non_positive_amounts(long amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TransferPolicy.Evaluate(1_000, 0, amount, Limit));
    }
}

public class RequestHasherTests
{
    private static readonly Guid Source = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Destination = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Idempotency: an identical payload produces the same request hash.
    [Fact]
    public void Same_payload_produces_same_hash()
    {
        Assert.Equal(
            RequestHasher.Transfer(Source, Destination, 1_000, "NGN", "rent"),
            RequestHasher.Transfer(Source, Destination, 1_000, "NGN", "rent"));
    }

    // Idempotency: changing any field changes the request hash.
    [Fact]
    public void Any_field_change_produces_a_different_hash()
    {
        var baseline = RequestHasher.Transfer(Source, Destination, 1_000, "NGN", "rent");

        Assert.NotEqual(baseline, RequestHasher.Transfer(Destination, Source, 1_000, "NGN", "rent"));
        Assert.NotEqual(baseline, RequestHasher.Transfer(Source, Destination, 1_001, "NGN", "rent"));
        Assert.NotEqual(baseline, RequestHasher.Transfer(Source, Destination, 1_000, "NGN", "rent2"));
        Assert.NotEqual(baseline, RequestHasher.Transfer(Source, Destination, 1_000, "NGN", null));
    }
}
