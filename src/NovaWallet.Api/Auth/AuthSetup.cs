using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Application.Common;

namespace NovaWallet.Api.Auth;

public static class AuthPolicies
{
    public const string Customer = "customer";
    public const string PostCredits = "post-credits";
}

public static class AuthSetup
{
    public static IServiceCollection AddJwtAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => o.SigningKeyBytes().Length >= 32, "Jwt:SigningKey must be at least 32 bytes.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
                "Jwt:Issuer and Jwt:Audience are required.")
            .ValidateOnStart();

        services.Configure<DevAuthOptions>(configuration.GetSection(DevAuthOptions.SectionName));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(jwt.SigningKeyBytes()),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Customer, policy => policy
                .RequireAuthenticatedUser()
                // Must fit customer_id varchar(128); an identity provider's oversized subject is rejected, not a 500.
                .RequireAssertion(context => context.User.FindFirst("sub")?.Value is { Length: > 0 and <= 128 } sub
                    && !string.IsNullOrWhiteSpace(sub)))
            .AddPolicy(AuthPolicies.PostCredits, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.GetScopes().Contains(LedgerScopes.PostCredits)));

        services.AddSingleton<DevTokenIssuer>();
        return services;
    }

    public static Caller ToCaller(this ClaimsPrincipal user)
    {
        var customerId = user.FindFirstValue("sub")
            ?? throw new InvalidOperationException("Authenticated principal has no 'sub' claim.");

        return new Caller(customerId, user.GetScopes());
    }

    private static HashSet<string> GetScopes(this ClaimsPrincipal user) =>
        user.FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
}
