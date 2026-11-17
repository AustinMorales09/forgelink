using ForgeLink.Application;
using ForgeLink.Application.Abstractions;
using ForgeLink.Contracts;
using ForgeLink.Infrastructure;
using ForgeLink.Infrastructure.Persistence;
using ForgeLink.UI;
using ForgeLink.Web.Api;
using ForgeLink.Web.Components;
using ForgeLink.Web.Realtime;
using ForgeLink.Web.Security;
using ForgeLink.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Application + adapters ----
builder.Services.AddForgeLinkApplication(builder.Configuration);
builder.Services.AddForgeLinkInfrastructure(builder.Configuration);

// ---- Identity: Entra ID (JWT for APIs, OIDC for the UI), API keys for devices, dev scheme locally ----
builder.Services.AddForgeLinkSecurity(builder.Configuration, builder.Environment);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserAccessor>());

// ---- Web surface ----
builder.Services.AddControllersWithViews();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSignalR();
builder.Services.AddSingleton<InProcessShopFloorEvents>();
builder.Services.AddSingleton<IShopFloorEvents>(sp => sp.GetRequiredService<InProcessShopFloorEvents>());
builder.Services.AddSingleton<IShopFloorNotifier, SignalRShopFloorNotifier>();
builder.Services.AddScoped<IForgeLinkApi, InProcessForgeLinkApi>();
builder.Services.AddScoped<IdempotencyFilter>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddForgeLinkApiPlatform(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<ForgeLinkDbContext>("database", tags: ["ready"]);

var app = builder.Build();

await app.Services.InitializeDatabaseAsync();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapForgeLinkApiDocs();

app.MapControllers();
app.MapControllerRoute("integration", "integration/{action=Index}/{id?}", new { controller = "Integration" });
app.MapHub<ShopFloorHub>("/" + ShopFloorHubMethods.Path);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(UiAssembly).Assembly);

app.Run();

/// <summary>Exposed for WebApplicationFactory integration tests.</summary>
public partial class Program;
