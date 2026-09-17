using System.Text;

namespace NovaWallet.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string SigningKey { get; init; } = string.Empty;

    public int AccessTokenLifetimeMinutes { get; init; } = 60;

    public byte[] SigningKeyBytes() => Encoding.UTF8.GetBytes(SigningKey);
}

public sealed class DevAuthOptions
{
    public const string SectionName = "DevAuth";

    public bool Enabled { get; init; }
}
