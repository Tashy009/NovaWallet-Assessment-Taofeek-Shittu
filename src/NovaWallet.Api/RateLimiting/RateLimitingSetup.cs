using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace NovaWallet.Api.RateLimiting;

public sealed class TransferRateLimitOptions
{
    public const string SectionName = "RateLimiting:Transfers";

    public int PermitLimit { get; init; } = 20;

    public int WindowSeconds { get; init; } = 60;
}

public static class RateLimitingSetup
{
    public const string TransfersPolicy = "transfers";

    public static IServiceCollection AddTransferRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TransferRateLimitOptions>(configuration.GetSection(TransferRateLimitOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Partitioned by the authenticated subject, so one customer cannot exhaust another's allowance.
            limiter.AddPolicy(TransfersPolicy, httpContext =>
            {
                var options = httpContext.RequestServices.GetRequiredService<IOptions<TransferRateLimitOptions>>().Value;
                var partitionKey = httpContext.User.FindFirst("sub")?.Value
                    ?? $"anonymous:{httpContext.Connection.RemoteIpAddress}";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            });

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too Many Requests",
                        Detail = "Transfer rate limit exceeded. Retry after the indicated delay.",
                        Type = "urn:novawallet:error:rate_limited",
                        Extensions = { ["errorCode"] = "rate_limited" },
                    },
                });
            };
        });

        return services;
    }
}
