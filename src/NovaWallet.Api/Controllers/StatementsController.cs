using Microsoft.AspNetCore.Mvc;
using NovaWallet.Api.Contracts;
using NovaWallet.Application.Statements;

namespace NovaWallet.Api.Controllers;

[Route("api/v1/wallets/{walletId:guid}/transactions")]
[Tags("Statements")]
public sealed class StatementsController(IStatementService statementService) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<StatementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StatementResponse>> Get(
        Guid walletId,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await statementService.GetPageAsync(CurrentCaller, walletId, limit, cursor, cancellationToken);
        if (!result.IsSuccess)
        {
            return ErrorResult(result.Error!);
        }

        var page = result.Value;
        return Ok(new StatementResponse(
            page.WalletId,
            page.Items.Select(i => new StatementItemResponse(i.TransactionId, i.Type, i.Direction, i.Currency, i.AmountKobo,
                i.BalanceAfterKobo, i.CounterpartyWalletId, i.ExternalReference, i.Narration, i.CreatedAt)).ToList(),
            page.NextCursor));
    }
}
