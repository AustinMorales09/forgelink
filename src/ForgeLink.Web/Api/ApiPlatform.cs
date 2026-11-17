using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace ForgeLink.Web.Api;

/// <summary>Per-caller request budgets for the REST API.</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Budget for ordinary API calls, per caller per minute.</summary>
    public int PermitsPerMinute { get; set; } = 600;

    /// <summary>Gateways batch machine signals at a high rate, so telemetry ingestion gets its own, larger budget.</summary>
    public int TelemetryPermitsPerMinute { get; set; } = 6000;
}

/// <summary>Cross-cutting API plumbing: the OpenAPI document, the API reference UI and rate limiting.</summary>
public static class ApiPlatform
{
    public const string TelemetryPath = "/api/machines/signals";

    public static IServiceCollection AddForgeLinkApiPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "ForgeLink API",
                Version = "v1",
                Description =
                    "Shop-floor to ERP integration API: work orders, inventory, quality, lot trace, machine telemetry and KPIs.\n\n" +
                    "**Authentication**: devices send `X-Api-Key`; people use an Entra ID bearer token. " +
                    "**Retries**: every command takes an `Idempotency-Key` header, so a retry never double-posts. " +
                    "**Errors**: RFC 9457 problem details with a stable `code` field. " +
                    "**Limits**: per-caller budgets; a `429` carries `Retry-After`.",
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Api-Key",
                Description = "Device key (stations, handhelds, gateways).",
            };
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Microsoft Entra ID access token.",
            };
            return Task.CompletedTask;
        }));

        var limits = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddRateLimiter(o =>
        {
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => PartitionFor(ctx, limits));
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(60);
                var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests",
                        Detail = $"Request budget used up for this caller. Retry in {seconds} s.",
                        Extensions = { ["code"] = "rate-limited", ["traceId"] = http.TraceIdentifier },
                    },
                });
            };
        });
        return services;
    }

    /// <summary>
    /// Token bucket per caller: authenticated identity (device or user), falling back to the client IP.
    /// Only /api is limited; the dashboard, health probes and static files are not.
    /// </summary>
    internal static RateLimitPartition<string> PartitionFor(HttpContext ctx, RateLimitOptions limits)
    {
        if (!limits.Enabled || !ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter("unlimited");

        var caller = ctx.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? "user:" + name
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        var telemetry = ctx.Request.Path.StartsWithSegments(TelemetryPath, StringComparison.OrdinalIgnoreCase);
        var perMinute = Math.Max(1, telemetry ? limits.TelemetryPermitsPerMinute : limits.PermitsPerMinute);

        return RateLimitPartition.GetTokenBucketLimiter((telemetry ? "telemetry|" : "api|") + caller, _ => new TokenBucketRateLimiterOptions
        {
            // Full minute's budget available as a burst, refilled smoothly every 6 seconds.
            TokenLimit = perMinute,
            TokensPerPeriod = Math.Max(1, perMinute / 10),
            ReplenishmentPeriod = TimeSpan.FromSeconds(6),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <summary>OpenAPI JSON at /openapi/v1.json and an interactive reference at /docs (both anonymous, read-only).</summary>
    public static WebApplication MapForgeLinkApiDocs(this WebApplication app)
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference("/docs", o => o
                .WithTitle("ForgeLink API")
                .AddPreferredSecuritySchemes("ApiKey")
                // A plain reference page: no vendor toolbar, AI agent, MCP promo or usage telemetry.
                .HideDeveloperTools()
                .HideClientButton()
                .DisableAgent()
                .DisableMcp()
                .DisableTelemetry())
            .AllowAnonymous();
        return app;
    }
}
