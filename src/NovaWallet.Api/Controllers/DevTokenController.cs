using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Contracts;

namespace NovaWallet.Api.Controllers;

// Mock token issuer for local demos and tests; returns 404 unless DevAuth:Enabled=true.
[Route("api/v1/dev/token")]
[Tags("Dev (local demo only)")]
[AllowAnonymous]
public sealed class DevTokenController(DevTokenIssuer issuer, IOptions<DevAuthOptions> devAuth) : ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType<DevTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<DevTokenResponse> Issue(DevTokenRequest request)
    {
        if (!devAuth.Value.Enabled)
        {
            return NotFound();
        }

        var customerId = request.CustomerId?.Trim();
        if (string.IsNullOrEmpty(customerId) || customerId.Length > 128 || customerId.Any(char.IsWhiteSpace))
        {
            ModelState.AddModelError("customerId", "Required; at most 128 characters with no whitespace.");
            return ValidationProblem(ModelState);
        }

        var (token, expiresAt) = issuer.Issue(customerId, request.Scopes ?? []);
        return Ok(new DevTokenResponse(token, "Bearer", expiresAt));
    }
}
