using Serilog;

namespace NovaWallet.Api.Observability;

// Adds the error code to the request completion log line so 4xx/5xx entries say why they failed.
public static class RequestLogEnricher
{
    private const string ErrorCodeItemKey = "NovaWallet.ErrorCode";

    public static void SetErrorCode(HttpContext context, string errorCode) => context.Items[ErrorCodeItemKey] = errorCode;

    public static void Enrich(IDiagnosticContext diagnosticContext, HttpContext context)
    {
        if (context.Items.TryGetValue(ErrorCodeItemKey, out var value) && value is string errorCode)
        {
            diagnosticContext.Set("ErrorCode", errorCode);
        }
    }
}
