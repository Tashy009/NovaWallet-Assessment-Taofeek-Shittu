using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace NovaWallet.Api.Auth;

// Mock issuer for local demos and tests; production tokens come from the real identity provider.
public sealed class DevTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public (string AccessToken, DateTimeOffset ExpiresAt) Issue(string customerId, IEnumerable<string> scopes)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(jwt.AccessTokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new("sub", customerId),
            new("jti", Guid.NewGuid().ToString("N")),
        };

        var scopeList = scopes.Distinct(StringComparer.Ordinal).ToArray();
        if (scopeList.Length > 0)
        {
            claims.Add(new Claim("scope", string.Join(' ', scopeList)));
        }

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(jwt.SigningKeyBytes()), SecurityAlgorithms.HmacSha256),
        });

        return (token, expiresAt);
    }
}
