using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Equipment;
using ForgeLink.Application.Integration;
using ForgeLink.Application.Inventory;
using ForgeLink.Application.Production;
using ForgeLink.Application.Quality;
using ForgeLink.Application.Reporting;
using ForgeLink.Contracts;
using ForgeLink.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace ForgeLink.Web.Services;

/// <summary>
/// <see cref="IForgeLinkApi"/> for the Blazor Server host: calls application services directly (no HTTP hop).
/// Each call gets its own DI scope, so a long-lived circuit never holds a stale DbContext, and the circuit's
/// user is attached for auditing and role checks.
/// </summary>
public sealed class InProcessForgeLinkApi(IServiceScopeFactory scopes, AuthenticationStateProvider authState) : IForgeLinkApi
{
    private async Task<T> Run<T>(Func<IServiceProvider, Task<T>> action, string? requiredRole = null)
    {
        var user = (await authState.GetAuthenticationStateAsync()).User;
        // Mirrors the HTTP policies: supervisors can do operator work; admins can do anything.
        var allowed = requiredRole is null || user.IsInRole(requiredRole) || user.IsInRole(Roles.Admin)
            || (requiredRole == Roles.Operator && user.IsInRole(Roles.Supervisor));
        if (!allowed)
            throw new UnauthorizedAccessException($"The {requiredRole} role is required.");

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUserAccessor>().Principal = user;
        return await action(scope.ServiceProvider);
    }

    private static T S<T>(IServiceProvider sp) where T : notnull => sp.GetRequiredService<T>();

    public Task<IReadOnlyList<MachineStatusDto>> GetMachinesAsync(string? line = null, CancellationToken ct = default) =>
        Run(sp => S<TelemetryService>(sp).GetMachinesAsync(line, ct));

    public Task<LineKpiDto> GetLineKpisAsync(string line, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        Run(sp => S<KpiService>(sp).GetLineKpisAsync(line, fromUtc, toUtc, ct));

    public Task<IReadOnlyList<DailyOeeDto>> GetDailyOeeAsync(string? line, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Run(sp => S<KpiRollupService>(sp).GetDailyAsync(line, from, to, ct));

    public Task<IntegrationHealthDto> GetIntegrationHealthAsync(CancellationToken ct = default) =>
        Run(sp => S<IntegrationAdminService>(sp).GetHealthAsync(ct));

    public Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync(string? line = null, CancellationToken ct = default) =>
        Run(sp => S<WorkOrderService>(sp).ListAsync(line, includeClosed: false, ct));

    public Task<WorkOrderDto?> GetWorkOrderAsync(string number, CancellationToken ct = default) =>
        Run(sp => S<WorkOrderService>(sp).GetAsync(number, ct));

    public Task<WorkOrderDto> StartWorkOrderAsync(string number, StartWorkOrderRequest request, CancellationToken ct = default) =>
        Run(sp => S<WorkOrderService>(sp).StartAsync(number, request, ct), Roles.Operator);

    public Task<WorkOrderDto> StopWorkOrderAsync(string number, StopWorkOrderRequest request, CancellationToken ct = default) =>
        Run(sp => S<WorkOrderService>(sp).StopAsync(number, request, ct), Roles.Operator);

    public Task<ProductionReportResult> ReportProductionAsync(string number, ReportProductionRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        Run(sp => S<WorkOrderService>(sp).ReportProductionAsync(number, request, ct), Roles.Operator);

    public Task<IReadOnlyList<InventoryBalanceDto>> GetInventoryAsync(string? sku = null, string? location = null, CancellationToken ct = default) =>
        Run(sp => S<InventoryService>(sp).QueryAsync(sku, location, ct));

    public Task<IReadOnlyList<InventoryTransactionDto>> GetRecentTransactionsAsync(int take = 50, CancellationToken ct = default) =>
        Run(sp => S<InventoryService>(sp).RecentTransactionsAsync(take, ct));

    public Task<InventoryTransactionDto> MoveInventoryAsync(MoveInventoryRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        Run(sp => S<InventoryService>(sp).MoveAsync(request, ct), Roles.Operator);

    public Task<CycleCountDto> SubmitCycleCountAsync(SubmitCycleCountRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        Run(sp => S<InventoryService>(sp).SubmitCycleCountAsync(request, ct), Roles.Operator);

    public Task<IReadOnlyList<CycleCountDto>> GetPendingCycleCountsAsync(CancellationToken ct = default) =>
        Run(sp => S<InventoryService>(sp).PendingCycleCountsAsync(ct));

    public Task<CycleCountDto> ApproveCycleCountAsync(Guid id, bool approve, CancellationToken ct = default) =>
        Run(sp => S<InventoryService>(sp).ReviewCycleCountAsync(id, approve, ct), Roles.Supervisor);

    public Task<QualityCheckDto> RecordQualityCheckAsync(RecordQualityCheckRequest request, string? idempotencyKey = null, CancellationToken ct = default) =>
        Run(sp => S<QualityService>(sp).RecordAsync(request, ct), Roles.Operator);

    public Task<LotTraceDto> TraceLotAsync(string lotNumber, CancellationToken ct = default) =>
        Run(sp => S<LotTraceService>(sp).TraceAsync(lotNumber, ct));
}
