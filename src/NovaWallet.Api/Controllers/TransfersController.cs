using Microsoft.AspNetCore.Mvc;
using NovaWallet.Api.Contracts;
using NovaWallet.Application.Transfers;

namespace NovaWallet.Api.Controllers;

[Route("api/v1/transfers")]
[Tags("Transfers")]
public sealed class TransfersController(TransferService transferService) : ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType<TransferResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TransferResponse>> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        TransferRequest request,
        CancellationToken cancellationToken)
    {
        var result = await transferService.TransferAsync(
            CurrentCaller,
            new TransferCommand(request.SourceWalletId, request.DestinationWalletId, request.AmountKobo, request.Currency, request.Narration),
            idempotencyKey,
            CorrelationId,
            cancellationToken);

        if (!result.IsSuccess)
        {
            return ErrorResult(result.Error!);
        }

        var outcome = result.Value;
        if (outcome.Replayed)
        {
            Response.Headers[ReplayedHeader] = "true";
        }

        if (outcome.Rejection is not null)
        {
            return ErrorResult(outcome.Rejection);
        }

        var r = outcome.Receipt!;
        return StatusCode(StatusCodes.Status201Created, new TransferResponse(r.TransactionId, r.SourceWalletId,
            r.DestinationWalletId, r.Currency, r.AmountKobo, r.SourceBalanceAfterKobo, r.Narration, r.BusinessDate, r.CreatedAt));
    }
}
