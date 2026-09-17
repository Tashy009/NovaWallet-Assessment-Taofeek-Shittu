using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Observability;
using NovaWallet.Application.Common;

namespace NovaWallet.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthPolicies.Customer)]
public abstract class ApiControllerBase : ControllerBase
{
    public const string ReplayedHeader = "Idempotent-Replayed";

    protected Caller CurrentCaller => User.ToCaller();

    protected string CorrelationId => CorrelationIdMiddleware.Get(HttpContext);

    protected ObjectResult ErrorResult(AppError error)
    {
        var status = Errors.StatusCode(error.Kind);

        var type = $"urn:novawallet:error:{error.Code}";

        ProblemDetails problem;
        if (error.ValidationErrors is not null)
        {
            var modelState = new ModelStateDictionary();
            foreach (var (field, messages) in error.ValidationErrors)
            {
                foreach (var message in messages)
                {
                    modelState.AddModelError(field, message);
                }
            }

            problem = ProblemDetailsFactory.CreateValidationProblemDetails(
                HttpContext, modelState, status, detail: error.Message, type: type);
        }
        else
        {
            problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: error.Message, type: type);
        }

        problem.Extensions["errorCode"] = error.Code;
        foreach (var (key, value) in error.Details ?? new Dictionary<string, object?>())
        {
            problem.Extensions[key] = value;
        }

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
