using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ForgeLink.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace ForgeLink.Web.Security;

public static class Policies
{
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public const string Integration = "Integration";
}

public static class Schemes
{
    public const string Smart = "Smart";
    public const string ApiKey = "ApiKey";
    public const string Development = "Development";
}

/// <summary>
/// Authentication strategy:
/// <list type="bullet">
/// <item><b>EntraId</b>: Microsoft Entra ID. APIs accept bearer tokens (app roles in the "roles" claim);
/// the browser UI signs in with OpenID Connect + cookie.</item>
/// <item><b>API keys</b>: for unattended shop-floor devices (station PCs, OT gateways) that cannot do interactive sign-in.
/// Keys map to a device identity and roles, and are compared in constant time.</item>
/// <item><b>Development</b>: a fixed local identity, refused outside the Development environment.</item>
/// </list>
/// A policy scheme picks the right handler per request.
/// </summary>
public static class SecurityExtensions
{
    public static IServiceCollection AddForgeLinkSecurity(this IServiceCollection services, IConfiguration config, IWebHostEnvironment env)
    {
        var mode = config["Authentication:Mode"] ?? "Development";
        var entra = mode.Equals("EntraId", StringComparison.OrdinalIgnoreCase);

        if (!entra && !env.IsDevelopment() && !config.GetValue<bool>("Authentication:AllowDevelopmentModeOutsideDevelopment"))
            throw new InvalidOperationException("Authentication:Mode=Development is only allowed in the Development environment. Configure EntraId.");

        services.Configure<ApiKeyOptions>(config.GetSection("Authentication:ApiKeys"));

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = Schemes.Smart;
            o.DefaultChallengeScheme = Schemes.Smart;
        });

        auth.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(Schemes.ApiKey, null);

        if (entra)
        {
            var section = config.GetSection("Authentication:EntraId");
            var authority = $"{section["Instance"]?.TrimEnd('/') ?? "https://login.microsoftonline.com"}/{section["TenantId"]}/v2.0";

            auth.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = authority;
                o.Audience = section["Audience"] ?? $"api://{section["ClientId"]}";
                o.MapInboundClaims = false;
                o.TokenValidationParameters.RoleClaimType = "roles";
                o.TokenValidationParameters.NameClaimType = "preferred_username";
            });

            auth.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
            {
                o.ForwardChallenge = OpenIdConnectDefaults.AuthenticationScheme;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.SlidingExpiration = true;
            });

            auth.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.Authority = authority;
                o.ClientId = section["ClientId"];
                o.ClientSecret = section["ClientSecret"];
                o.ResponseType = "code";
                o.UsePkce = true;
                o.SaveTokens = false;
                o.MapInboundClaims = false;
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.TokenValidationParameters.RoleClaimType = "roles";
                o.TokenValidationParameters.NameClaimType = "preferred_username";
                o.Scope.Add("profile");
            });
        }
        else
        {
            auth.AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(Schemes.Development, null);
        }

        auth.AddPolicyScheme(Schemes.Smart, Schemes.Smart, o => o.ForwardDefaultSelector = ctx =>
        {
            if (ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)) return Schemes.ApiKey;
            if (!entra) return Schemes.Development;
            var header = ctx.Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Operator, p => p.RequireAuthenticatedUser().RequireRole(Roles.Operator, Roles.Supervisor, Roles.Admin))
            .AddPolicy(Policies.Supervisor, p => p.RequireAuthenticatedUser().RequireRole(Roles.Supervisor, Roles.Admin))
            .AddPolicy(Policies.Integration, p => p.RequireAuthenticatedUser().RequireRole(Roles.Integration, Roles.Admin))
            // Secure by default: every endpoint needs a signed-in user unless it opts out.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}

public sealed class ApiKeyOptions
{
    public List<ApiKeyEntry> Keys { get; set; } = [];
}

public sealed class ApiKeyEntry
{
    /// <summary>In production store only a SHA-256 hash (KeyHash) and keep the plaintext in Key Vault on the device.</summary>
    public string? Key { get; set; }

    public string? KeyHash { get; set; }
    public string Name { get; set; } = "";
    public List<string> Roles { get; set; } = [];
}

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptionsMonitor<ApiKeyOptions> keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(presented)) return Task.FromResult(AuthenticateResult.NoResult());

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        foreach (var entry in keys.CurrentValue.Keys)
        {
            var expectedHash = entry.KeyHash is { Length: > 0 }
                ? Convert.FromHexString(entry.KeyHash)
                : SHA256.HashData(Encoding.UTF8.GetBytes(entry.Key ?? ""));
            if (!CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash)) continue;

            var claims = new List<Claim> { new(ClaimTypes.Name, entry.Name), new("auth_method", "api_key") };
            claims.AddRange(entry.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }
}

/// <summary>Local-only identity. Override per request with X-Dev-User / X-Dev-Roles to exercise role rules.</summary>
public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = FirstNonEmpty(Request.Headers["X-Dev-User"], config["Authentication:Development:User"], "dev.supervisor");
        var roles = FirstNonEmpty(Request.Headers["X-Dev-Roles"], config["Authentication:Development:Roles"], "Supervisor,Operator,Integration");

        var claims = new List<Claim> { new(ClaimTypes.Name, user), new("auth_method", "development") };
        claims.AddRange(roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    private static string FirstNonEmpty(params string?[] values) => values.First(v => !string.IsNullOrWhiteSpace(v))!;
}

/// <summary>Resolves the acting user for auditing: HTTP request user, or the Blazor circuit user set explicitly.</summary>
public sealed class CurrentUserAccessor(IHttpContextAccessor http) : ICurrentUser
{
    public ClaimsPrincipal? Principal { get; set; }

    private ClaimsPrincipal? User => Principal ?? http.HttpContext?.User;

    public string Name => User?.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(User.Identity.Name) ? User.Identity.Name : "system";

    public bool IsInRole(string role) => User?.IsInRole(role) == true;
}
