using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeLink.Application.Common;
using ForgeLink.Domain;
using ForgeLink.Domain.Integration;
using ForgeLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.Web.Api;

/// <summary>Maps exceptions to RFC 9457 problem details with a stable <c>code</c> clients can branch on.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, code) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found", "not-found"),
            DomainException { Code: "validation" } => (StatusCodes.Status400BadRequest, "Invalid request", "validation"),
            DomainException d => (StatusCodes.Status422UnprocessableEntity, "Business rule violated", d.Code),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Changed by someone else", "concurrency"),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden", "forbidden"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "server-error"),
        };

        if (status >= 500) logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status >= 500 ? "The error has been logged." : exception is DbUpdateConcurrencyException
                    ? "The record was updated by another user or device. Reload and try again."
                    : exception.Message,
                Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier },
            },
        });
    }
}

/// <summary>Marks a command endpoint as requiring an Idempotency-Key header (safe client retries / offline replay).</summary>
public sealed class IdempotentAttribute() : ServiceFilterAttribute(typeof(IdempotencyFilter));

/// <summary>
/// Idempotency-Key handling (draft-ietf-httpapi-idempotency-key-header):
/// first call executes inside a transaction and stores the response with the key, atomically;
/// a repeat with the same key and body replays the stored response; same key + different body is rejected.
/// Keys are scoped to the caller so two devices cannot collide.
/// </summary>
public sealed class IdempotencyFilter(ForgeLinkDbContext db, TimeProvider clock, ILogger<IdempotencyFilter> logger) : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            context.Result = Problem(StatusCodes.Status400BadRequest, "idempotency-key-required",
                $"This operation requires an {HeaderName} header (1-100 characters). Generate one per user action and reuse it on retry.");
            return;
        }

        var scopedKey = $"{http.User.Identity?.Name ?? "anonymous"}:{key}";
        // Hash the bound request (route + body), not infrastructure arguments like CancellationToken.
        var arguments = context.ActionArguments.Where(a => a.Value is not CancellationToken).OrderBy(a => a.Key).ToDictionary();
        var requestHash = Hash($"{http.Request.Method} {http.Request.Path}\n{JsonSerializer.Serialize(arguments)}");

        var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == scopedKey, http.RequestAborted);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
            {
                context.Result = Problem(StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused",
                    "This Idempotency-Key was already used for a different request.");
                return;
            }

            http.Response.Headers[ReplayedHeader] = "true";
            context.Result = new ContentResult { StatusCode = existing.StatusCode, Content = existing.ResponseBody, ContentType = "application/json" };
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled) return; // disposing the transaction rolls back

        if (executed.Result is ObjectResult { Value: var value } result && (result.StatusCode ?? 200) is >= 200 and < 300)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord(scopedKey, requestHash, result.StatusCode ?? 200,
                JsonSerializer.Serialize(value, JsonSerializerOptions.Web), clock.GetUtcNow().UtcDateTime));
            try
            {
                await db.SaveChangesAsync(http.RequestAborted);
            }
            catch (DbUpdateException ex)
            {
                // A concurrent request with the same key committed first. Roll back this one entirely.
                logger.LogWarning(ex, "Concurrent duplicate for idempotency key {Key}", scopedKey);
                await transaction.RollbackAsync(http.RequestAborted);
                executed.Result = Problem(StatusCodes.Status409Conflict, "idempotency-in-progress",
                    "A request with this Idempotency-Key is already being processed. Retry shortly.");
                return;
            }
        }

        await transaction.CommitAsync(http.RequestAborted);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ObjectResult Problem(int status, string code, string detail) => new(new ProblemDetails
    {
        Status = status,
        Title = "Idempotency",
        Detail = detail,
        Extensions = { ["code"] = code },
    })
    { StatusCode = status };
}
