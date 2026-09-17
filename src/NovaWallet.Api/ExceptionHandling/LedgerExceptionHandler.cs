using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Application.Abstractions;

namespace NovaWallet.Api.ExceptionHandling;

public sealed class LedgerExceptionHandler(IProblemDetailsService problemDetails, ILogger<LedgerExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            TransientConcurrencyException => (StatusCodes.Status503ServiceUnavailable, "concurrency_conflict",
                "The request conflicted with concurrent activity and was rolled back. Retry with the same Idempotency-Key."),
            LedgerInvariantViolationException => (StatusCodes.Status500InternalServerError, "ledger_invariant_violation",
                "The operation was rolled back; no funds were moved."),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred."),
        };

        if (status >= 500 && exception is not TransientConcurrencyException)
        {
            logger.LogError(exception, "Unhandled exception {ErrorCode}", code);
        }
        else
        {
            logger.LogWarning("Request rolled back: {ErrorCode}", code);
        }

        httpContext.Response.StatusCode = status;
        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status == 503 ? "Service Unavailable" : "Internal Server Error",
                Detail = detail,
                Type = $"urn:novawallet:error:{code}",
                Extensions = { ["errorCode"] = code },
            },
        });
    }
}
