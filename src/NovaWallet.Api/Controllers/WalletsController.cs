using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Contracts;
using NovaWallet.Application.Credits;
using NovaWallet.Application.Wallets;

namespace NovaWallet.Api.Controllers;

[Route("api/v1/wallets")]
[Tags("Wallets")]
public sealed class WalletsController(IWalletService walletService, ICreditService creditService) : ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType<WalletResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WalletResponse>> Create(CancellationToken cancellationToken)
    {
        var result = await walletService.CreateAsync(CurrentCaller, cancellationToken);
        if (!result.IsSuccess)
        {
            return ErrorResult(result.Error!);
        }

        var wallet = result.Value;
        return CreatedAtAction(
            nameof(GetBalance),
            new { walletId = wallet.WalletId },
            new WalletResponse(wallet.WalletId, wallet.CustomerId, wallet.Currency, wallet.BalanceKobo,
                wallet.DailyOutboundLimitKobo, wallet.Status, wallet.CreatedAt));
    }

    [HttpGet("{walletId:guid}/balance")]
    [ProducesResponseType<BalanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BalanceResponse>> GetBalance(Guid walletId, CancellationToken cancellationToken)
    {
        var result = await walletService.GetBalanceAsync(CurrentCaller, walletId, cancellationToken);
        if (!result.IsSuccess)
        {
            return ErrorResult(result.Error!);
        }

        return Ok(new BalanceResponse(result.Value.WalletId, result.Value.Currency, result.Value.BalanceKobo));
    }

    [HttpPost("{walletId:guid}/credits")]
    [Authorize(Policy = AuthPolicies.PostCredits)]
    [ProducesResponseType<CreditResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CreditResponse>> Credit(
        Guid walletId, CreditWalletRequest request, CancellationToken cancellationToken)
    {
        var result = await creditService.CreditAsync(
            CurrentCaller,
            new CreditWalletCommand(walletId, request.AmountKobo, request.ExternalReference, request.Narration),
            CorrelationId,
            cancellationToken);

        if (!result.IsSuccess)
        {
            return ErrorResult(result.Error!);
        }

        var receipt = result.Value.Receipt;
        var response = new CreditResponse(receipt.TransactionId, receipt.WalletId, receipt.Currency, receipt.AmountKobo,
            receipt.BalanceAfterKobo, receipt.ExternalReference, receipt.Narration, receipt.CreatedAt);

        if (result.Value.Replayed)
        {
            Response.Headers[ReplayedHeader] = "true";
        }

        return CreatedAtAction(nameof(GetBalance), new { walletId = receipt.WalletId }, response);
    }
}
