using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog.Context;

namespace NovaWallet.Api.Observability;

public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ItemKey = "NovaWallet.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();

        // Client values reach logs and the audit trail, so anything malformed is replaced rather than trusted.
        var correlationId = supplied.Length is > 0 and <= 64 && SafePattern().IsMatch(supplied)
            ? supplied
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;

    [GeneratedRegex("^[A-Za-z0-9_.:-]+$")]
    private static partial Regex SafePattern();
}
