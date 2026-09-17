using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Api.Auth;

namespace NovaWallet.IntegrationTests.Infrastructure;

// Hand-built JWTs for negative authentication tests; defaults match what the API accepts.
public static class TestTokens
{
    public static string Create(
        ApiFixture fixture, string? subject = "cust-token-test", string? issuer = null, string? audience = null,
        string? signingKey = null, DateTime? expiresUtc = null)
    {
        var jwt = fixture.Factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var expires = expiresUtc ?? DateTime.UtcNow.AddMinutes(5);
        var claims = new Dictionary<string, object>();
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? jwt.Issuer,
            Audience = audience ?? jwt.Audience,
            Claims = claims,
            IssuedAt = expires.AddMinutes(-10),
            NotBefore = expires.AddMinutes(-10),
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? jwt.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    public static string Unsigned(ApiFixture fixture, string subject)
    {
        var jwt = fixture.Factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode($$"""{"sub":"{{subject}}","iss":"{{jwt.Issuer}}","aud":"{{jwt.Audience}}","exp":{{exp}}}""");
        return $"{header}.{payload}.";
    }
}
