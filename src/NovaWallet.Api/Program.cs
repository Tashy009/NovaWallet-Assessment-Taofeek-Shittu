using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using NovaWallet.Api.Auth;
using NovaWallet.Api.ExceptionHandling;
using NovaWallet.Api.Observability;
using NovaWallet.Api.RateLimiting;
using NovaWallet.Application;
using NovaWallet.Infrastructure;
using NovaWallet.Infrastructure.Migrations;
using NovaWallet.Infrastructure.Persistence;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs; request bodies and headers are never logged.
builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);

// Reject unknown fields and non-integer amounts instead of silently ignoring or coercing them.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    });

builder.Services.AddExceptionHandler<LedgerExceptionHandler>();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        context.ProblemDetails.Extensions["correlationId"] = CorrelationIdMiddleware.Get(context.HttpContext);
    };
});

builder.Services.AddTransferRateLimiting(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NovaWallet Ledger Service",
        Version = "v1",
        Description = "Wallet ledger service for NovaWallet. All monetary amounts are integers in kobo (1 NGN = 100 kobo).",
    });

    var bearer = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    options.AddSecurityDefinition("Bearer", bearer);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = [] });
});

var app = builder.Build();

// Order matters: correlation id wraps everything, and request logging sits outside the exception handler
// so the logged status is the one the client actually received.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options => options.EnrichDiagnosticContext = RequestLogEnricher.Enrich);
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "NovaWallet Ledger v1"));
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(NovaWallet.Infrastructure.DependencyInjection.ReadyTag),
}).AllowAnonymous();

app.MapControllers();

// Fail fast: never serve traffic on an unmigrated schema.
if (app.Configuration.GetSection(DatabaseOptions.SectionName).GetValue(nameof(DatabaseOptions.RunMigrationsOnStartup), true))
{
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(app.Lifetime.ApplicationStopping);
}

await app.RunAsync();

public partial class Program;
