using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Application.Equipment;
using ForgeLink.Application.Integration;
using ForgeLink.Application.Inventory;
using ForgeLink.Application.Production;
using ForgeLink.Application.Quality;
using ForgeLink.Application.Reporting;
using ForgeLink.Contracts;
using ForgeLink.Domain;
using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Integration;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using ForgeLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForgeLink.UnitTests.Application;

/// <summary>Use-case tests against a real SQLite database seeded with the demo plant.</summary>
public sealed class ApplicationTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    private readonly FakeClock _clock = new(Start);
    private readonly TestDb _db;
    private readonly TestUser _operator = new("op.alice", Roles.Operator);

    public ApplicationTests() => _db = new TestDb(_clock);

    public void Dispose() => _db.Dispose();

    private WorkOrderService WorkOrders(ForgeLinkDbContext db, ICurrentUser? user = null) =>
        new(db, user ?? _operator, _clock, new NullShopFloorNotifier());

    private InventoryService Inventory(ForgeLinkDbContext db, ICurrentUser? user = null) =>
        new(db, user ?? _operator, _clock, new NullShopFloorNotifier(), Options.Create(new ForgeLinkOptions { CycleCountTolerancePercent = 2 }));

    private async Task StartWo24001Async()
    {
        await using var db = _db.CreateContext(_operator);
        await WorkOrders(db).StartAsync("WO-24001", new StartWorkOrderRequest("L1-CNC-01", "EMP-7"), default);
    }

    // ---------------- production ----------------

    [Fact]
    public async Task Production_report_updates_everything_atomically_and_stages_one_erp_message()
    {
        await StartWo24001Async();

        await using (var db = _db.CreateContext(_operator))
        {
            var result = await WorkOrders(db).ReportProductionAsync("WO-24001",
                new ReportProductionRequest(24, 2, "DIMENSIONAL", null, [new ConsumedLotDto("HEAT-88231", 31.5m)]), default);
            Assert.Equal("WO-24001-261109", result.OutputLotNumber);
            Assert.Equal(376, result.QuantityRemaining);
        }

        await using var check = _db.CreateContext();
        Assert.Equal(24, (await check.InventoryBalances.SingleAsync(b => b.Location == "L1-OUT")).Quantity);
        Assert.Equal(800 - 31.5m, (await check.InventoryBalances.SingleAsync(b => b.LotNumber == "HEAT-88231")).Quantity);
        Assert.Equal(3, await check.InventoryTransactions.CountAsync()); // issue + receipt + scrap
        Assert.Single(await check.LotGenealogy.ToListAsync(), g => g.ParentLot == "HEAT-88231" && g.ChildLot == "WO-24001-261109");

        var outbox = await check.OutboxMessages.SingleAsync(m => m.MessageType == ErpMessageTypes.ProductionReceipt);
        Assert.Equal("wo:WO-24001", outbox.PartitionKey);
        Assert.Contains("\"goodQuantity\":24", outbox.Payload);
        Assert.Equal("op.alice", outbox.CreatedBy);

        // Audit trail captured the work-order change with before/after values and the user.
        var audit = await check.AuditEntries.Where(a => a.EntityType == nameof(WorkOrder) && a.Action == "Modified").ToListAsync();
        Assert.Contains(audit, a => a.UserName == "op.alice" && a.Changes.Contains("QuantityCompleted"));
    }

    [Fact]
    public async Task Failed_report_leaves_no_partial_state_or_outbox_message()
    {
        await StartWo24001Async();
        await using (var db = _db.CreateContext(_operator))
        {
            var lot = await db.Lots.SingleAsync(l => l.LotNumber == "HEAT-88231");
            lot.Hold("Mill cert missing");
            await db.SaveChangesAsync();
        }

        await using (var db = _db.CreateContext(_operator))
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() => WorkOrders(db).ReportProductionAsync("WO-24001",
                new ReportProductionRequest(10, 0, ConsumedLots: [new ConsumedLotDto("HEAT-88231", 5)]), default));
            Assert.Equal("lot.on-hold", ex.Code);
        }

        await using var check = _db.CreateContext();
        Assert.Equal(0, (await check.WorkOrders.SingleAsync(w => w.Number == "WO-24001")).QuantityCompleted);
        Assert.False(await check.OutboxMessages.AnyAsync(m => m.MessageType == ErpMessageTypes.ProductionReceipt));
        Assert.False(await check.InventoryTransactions.AnyAsync());
    }

    [Fact]
    public async Task Start_rejects_machine_on_another_line_and_pause_closes_labor_to_erp()
    {
        await using (var db = _db.CreateContext(_operator))
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                WorkOrders(db).StartAsync("WO-24001", new StartWorkOrderRequest("L2-CNC-01", null), default));
            Assert.Equal("workorder.wrong-line", ex.Code);
        }

        await StartWo24001Async();
        _clock.Advance(TimeSpan.FromMinutes(90));
        await using (var db = _db.CreateContext(_operator))
            await WorkOrders(db).StopAsync("WO-24001", new StopWorkOrderRequest(false, "EMP-7"), default);

        await using var check = _db.CreateContext();
        var labor = await check.LaborEntries.SingleAsync();
        Assert.Equal(1.5m, labor.Hours);
        Assert.True(await check.OutboxMessages.AnyAsync(m => m.MessageType == ErpMessageTypes.Labor));
        Assert.Null((await check.Machines.SingleAsync(m => m.Code == "L1-CNC-01")).CurrentWorkOrder);
    }

    // ---------------- inventory & quality ----------------

    [Fact]
    public async Task Quality_failure_holds_the_lot_and_blocks_normal_moves_but_allows_quarantine()
    {
        await StartWo24001Async();
        await using (var db = _db.CreateContext(_operator))
            await WorkOrders(db).ReportProductionAsync("WO-24001", new ReportProductionRequest(10, 0), default);

        await using (var db = _db.CreateContext(_operator))
        {
            var q = await new QualityService(db, _operator, _clock, new NullShopFloorNotifier()).RecordAsync(
                new RecordQualityCheckRequest("WO-24001", "WO-24001-261109", "Bore", 42, 41.975m, 42.025m, 42.04m), default);
            Assert.Equal("Fail", q.Result);
        }

        await using (var db = _db.CreateContext(_operator))
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                Inventory(db).MoveAsync(new MoveInventoryRequest("BRK-1001", "WO-24001-261109", "L1-OUT", "FG-01", 10), default));
            Assert.Equal("lot.on-hold", ex.Code);
        }

        await using (var db = _db.CreateContext(_operator))
            await Inventory(db).MoveAsync(new MoveInventoryRequest("BRK-1001", "WO-24001-261109", "L1-OUT", "QA-HOLD", 10), default);

        await using var check = _db.CreateContext();
        Assert.Equal(10, (await check.InventoryBalances.SingleAsync(b => b.Location == "QA-HOLD")).Quantity);
        Assert.True(await check.OutboxMessages.AnyAsync(m => m.MessageType == ErpMessageTypes.QualityHold));
        Assert.True(await check.OutboxMessages.AnyAsync(m => m.MessageType == ErpMessageTypes.InventoryTransfer && m.PartitionKey == "inv:BRK-1001"));
    }

    [Fact]
    public async Task Cycle_count_inside_tolerance_posts_outside_needs_another_persons_approval()
    {
        await using (var db = _db.CreateContext(_operator))
        {
            var ok = await Inventory(db).SubmitCycleCountAsync(new SubmitCycleCountRequest("AL6061-BAR-50", "L1-WIP", "HEAT-88231", 795), default);
            Assert.Equal("Posted", ok.Status); // -0.6 % within 2 %
        }

        Guid pendingId;
        await using (var db = _db.CreateContext(_operator))
        {
            var big = await Inventory(db).SubmitCycleCountAsync(new SubmitCycleCountRequest("AL6061-BAR-50", "RM-01", "HEAT-88233", 1000), default);
            Assert.Equal("PendingApproval", big.Status);
            pendingId = big.Id;
        }

        await using (var db = _db.CreateContext(_operator))
            await Assert.ThrowsAsync<DomainException>(() => Inventory(db).ReviewCycleCountAsync(pendingId, true, default));

        var supervisor = new TestUser("sup.bob", Roles.Supervisor);
        await using (var db = _db.CreateContext(supervisor))
            await Inventory(db, supervisor).ReviewCycleCountAsync(pendingId, true, default);

        await using var check = _db.CreateContext();
        Assert.Equal(795, (await check.InventoryBalances.SingleAsync(b => b.LotNumber == "HEAT-88231")).Quantity);
        Assert.Equal(1000, (await check.InventoryBalances.SingleAsync(b => b.LotNumber == "HEAT-88233")).Quantity);
        Assert.Equal(2, await check.OutboxMessages.CountAsync(m => m.MessageType == ErpMessageTypes.InventoryAdjustment));
    }

    [Fact]
    public async Task Lot_trace_walks_components_and_where_used()
    {
        await StartWo24001Async();
        await using (var db = _db.CreateContext(_operator))
            await WorkOrders(db).ReportProductionAsync("WO-24001",
                new ReportProductionRequest(5, 0, ConsumedLots: [new ConsumedLotDto("HEAT-88231", 6.2m)]), default);

        await using var read = _db.CreateContext();
        var trace = new LotTraceService(read, Inventory(read));
        var forward = await trace.TraceAsync("HEAT-88231", default);
        Assert.Equal("WO-24001-261109", Assert.Single(forward.WhereUsed.Children).LotNumber);

        var backward = await trace.TraceAsync("WO-24001-261109", default);
        var component = Assert.Single(backward.Components.Children);
        Assert.Equal(("HEAT-88231", 6.2m), (component.LotNumber, component.LinkQuantity!.Value));
        Assert.Equal(5, Assert.Single(backward.OnHand).Quantity);
    }

    // ---------------- outbox delivery ----------------

    private sealed class FakeErp : IErpClient
    {
        public Func<string, string, ErpPostResult> Behaviour { get; set; } = (_, _) => new ErpPostResult(ErpPostOutcome.Accepted, "ERP-1");
        public List<string> Calls { get; } = [];
        public List<ErpWorkOrder> WorkOrders { get; } = [];
        public List<ErpItem> Items { get; } = [];
        public bool FailReads { get; set; }

        public Task<ErpPostResult> PostAsync(string messageType, string payloadJson, string idempotencyKey, CancellationToken ct)
        {
            Calls.Add(idempotencyKey);
            return Task.FromResult(Behaviour(messageType, idempotencyKey));
        }

        public Task<IReadOnlyList<ErpItem>> GetItemsChangedSinceAsync(DateTime sinceUtc, CancellationToken ct) =>
            FailReads ? throw new HttpRequestException("ERP down") : Task.FromResult<IReadOnlyList<ErpItem>>(Items.Where(i => i.ModifiedAtUtc >= sinceUtc).ToList());

        public Task<IReadOnlyList<ErpWorkOrder>> GetWorkOrdersChangedSinceAsync(DateTime sinceUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ErpWorkOrder>>(WorkOrders.Where(w => w.ModifiedAtUtc >= sinceUtc).ToList());

        public Task<ErpWorkOrder?> GetWorkOrderAsync(string number, CancellationToken ct) =>
            Task.FromResult(WorkOrders.FirstOrDefault(w => w.Number == number));
    }

    private OutboxProcessor Processor(ForgeLinkDbContext db, IErpClient erp, int maxAttempts = 5) =>
        new(db, erp, _clock, Options.Create(new OutboxOptions { BatchSize = 50, MaxAttempts = maxAttempts, BaseDelaySeconds = 10, MaxDelaySeconds = 600 }),
            NullLogger<OutboxProcessor>.Instance);

    private async Task SeedOutboxAsync(params (string Partition, string Key)[] messages)
    {
        await using var db = _db.CreateContext();
        foreach (var (partition, key) in messages)
            db.OutboxMessages.Add(new OutboxMessage(ErpMessageTypes.InventoryTransfer, partition, "{}", key, _clock.UtcNow));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Transient_failure_backs_off_then_succeeds_and_duplicate_counts_as_delivered()
    {
        await SeedOutboxAsync(("inv:A", "k1"), ("inv:B", "k2"));
        var erp = new FakeErp { Behaviour = (_, key) => key == "k1" ? new(ErpPostOutcome.TransientFailure, Error: "503") : new(ErpPostOutcome.Duplicate) };

        await using (var db = _db.CreateContext())
        {
            var r = await Processor(db, erp).ProcessBatchAsync(default);
            Assert.Equal((1, 1, 0), (r.Sent, r.Retrying, r.DeadLettered));
        }

        // Not due yet: nothing is attempted.
        await using (var db = _db.CreateContext())
            Assert.Equal(0, (await Processor(db, erp).ProcessBatchAsync(default)).Sent + erp.Calls.Count - 2);

        _clock.Advance(TimeSpan.FromSeconds(15));
        erp.Behaviour = (_, _) => new(ErpPostOutcome.Accepted, "ERP-9");
        await using (var db = _db.CreateContext())
            Assert.Equal(1, (await Processor(db, erp).ProcessBatchAsync(default)).Sent);

        await using var check = _db.CreateContext();
        var k1 = await check.OutboxMessages.SingleAsync(m => m.IdempotencyKey == "k1");
        Assert.Equal((OutboxStatus.Sent, 2, "ERP-9"), (k1.Status, k1.Attempts, k1.ErpReference));
    }

    [Fact]
    public async Task Dead_letter_blocks_its_partition_but_not_others_until_requeued()
    {
        await SeedOutboxAsync(("inv:A", "a1"), ("inv:A", "a2"), ("inv:B", "b1"));
        var erp = new FakeErp { Behaviour = (_, key) => key == "a1" ? new(ErpPostOutcome.PermanentFailure, Error: "Item not in ERP") : new(ErpPostOutcome.Accepted) };

        await using (var db = _db.CreateContext())
            await Processor(db, erp).ProcessBatchAsync(default);

        Assert.Equal(["a1", "b1"], erp.Calls); // a2 must wait behind a1 to preserve order

        await using (var db = _db.CreateContext())
        {
            Assert.Equal(OutboxStatus.DeadLettered, (await db.OutboxMessages.SingleAsync(m => m.IdempotencyKey == "a1")).Status);
            Assert.Equal(OutboxStatus.Pending, (await db.OutboxMessages.SingleAsync(m => m.IdempotencyKey == "a2")).Status);
        }

        // Operator fixes the ERP master data and retries.
        erp.Behaviour = (_, _) => new(ErpPostOutcome.Accepted);
        await using (var db = _db.CreateContext(new TestUser("integration.eve")))
        {
            var id = (await db.OutboxMessages.SingleAsync(m => m.IdempotencyKey == "a1")).Id;
            await new IntegrationAdminService(db, new TestUser("integration.eve"), _clock).RetryAsync(id, default);
        }
        await using (var db = _db.CreateContext())
            await Processor(db, erp).ProcessBatchAsync(default);

        Assert.Equal(["a1", "b1", "a1", "a2"], erp.Calls);
        await using var check = _db.CreateContext();
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == "Requeued" && a.UserName == "integration.eve"));
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        await SeedOutboxAsync(("inv:A", "x"));
        var erp = new FakeErp { Behaviour = (_, _) => new(ErpPostOutcome.TransientFailure, Error: "timeout") };
        for (var i = 0; i < 3; i++)
        {
            await using var db = _db.CreateContext();
            await Processor(db, erp, maxAttempts: 3).ProcessBatchAsync(default);
            _clock.Advance(TimeSpan.FromHours(1));
        }

        await using var check = _db.CreateContext();
        var m = await check.OutboxMessages.SingleAsync();
        Assert.Equal((OutboxStatus.DeadLettered, 3), (m.Status, m.Attempts));
    }

    // ---------------- ERP master data ----------------

    [Fact]
    public async Task Erp_sync_adds_new_orders_advances_watermark_and_reports_conflicts()
    {
        await StartWo24001Async();
        var erp = new FakeErp();
        erp.Items.Add(new ErpItem("BRK-2001", "Caliper rev B", "EA", 1.18m, true, Start.AddMinutes(1)));
        erp.WorkOrders.Add(new ErpWorkOrder("WO-24004", "BRK-2001", 150, "L1", Start.AddDays(3), "Released", Start.AddMinutes(1)));
        erp.WorkOrders.Add(new ErpWorkOrder("WO-24001", "BRK-1001", 400, "L1", Start.AddDays(1), "Cancelled", Start.AddMinutes(2)));

        ErpSyncService.SyncSummary summary;
        await using (var db = _db.CreateContext())
            summary = await new ErpSyncService(db, erp, _clock, NullLogger<ErpSyncService>.Instance).SyncMasterDataAsync(default);

        Assert.Equal((1, 2), (summary.Items, summary.WorkOrders));
        Assert.Contains("WO-24001", Assert.Single(summary.Conflicts));

        await using var check = _db.CreateContext();
        Assert.True(await check.WorkOrders.AnyAsync(w => w.Number == "WO-24004"));
        Assert.Equal(WorkOrderStatus.InProgress, (await check.WorkOrders.SingleAsync(w => w.Number == "WO-24001")).Status);
        Assert.Equal(Start.AddMinutes(2), (await check.SyncStates.SingleAsync(s => s.Name == ErpSyncService.WorkOrdersFeed)).WatermarkUtc);
    }

    [Fact]
    public async Task Erp_sync_failure_is_recorded_not_thrown()
    {
        var erp = new FakeErp { FailReads = true };
        await using (var db = _db.CreateContext())
            await new ErpSyncService(db, erp, _clock, NullLogger<ErpSyncService>.Instance).SyncMasterDataAsync(default);

        await using var check = _db.CreateContext();
        Assert.Equal("ERP down", (await check.SyncStates.SingleAsync(s => s.Name == ErpSyncService.ItemsFeed)).LastError);
    }

    // ---------------- OT telemetry & KPIs ----------------

    private TelemetryService Telemetry(ForgeLinkDbContext db) =>
        new(db, _clock, new NullShopFloorNotifier(), Options.Create(new ForgeLinkOptions { MachineHeartbeatTimeoutSeconds = 30 }), NullLogger<TelemetryService>.Instance);

    private async Task SignalAsync(MachineSignalDto s)
    {
        await using var db = _db.CreateContext();
        await Telemetry(db).HandleAsync(s, default);
    }

    [Fact]
    public async Task Telemetry_builds_downtime_counters_and_oee()
    {
        var t = Start;
        _clock.UtcNow = t.AddHours(2); // signals arrive with device timestamps in the past (gateway buffering)
        await SignalAsync(new("L1-CNC-01", "state", "Running", TimestampUtc: t));
        await SignalAsync(new("L1-CNC-01", "count", GoodDelta: 50, RejectDelta: 2, TimestampUtc: t.AddMinutes(40)));
        await SignalAsync(new("L1-CNC-01", "state", "Down", "TOOL_CHANGE", TimestampUtc: t.AddMinutes(45)));
        await SignalAsync(new("L1-CNC-01", "state", "Running", TimestampUtc: t.AddMinutes(60)));
        await SignalAsync(new("L1-CNC-01", "count", GoodDelta: 10, TimestampUtc: t.AddMinutes(70)));
        await SignalAsync(new("NOPE", "heartbeat")); // unknown machine is ignored, not fatal

        await using var db = _db.CreateContext();
        var downtime = await db.DowntimeEvents.SingleAsync();
        Assert.Equal(("TOOL_CHANGE", t.AddMinutes(60)), (downtime.ReasonCode, downtime.EndUtc!.Value));
        Assert.Equal(2, await db.ProductionCounters.CountAsync()); // 08:00 and 09:00 buckets

        var integration = new IntegrationAdminService(db, _operator, _clock);
        var kpi = await new KpiService(db, new EfDowntimeParetoQuery(db), integration).GetLineKpisAsync("L1", t, t.AddHours(2), default);
        var m = kpi.Machines.Single(x => x.MachineCode == "L1-CNC-01");
        Assert.Equal((60, 2, 15.0), (m.GoodCount, m.RejectCount, m.DowntimeMinutes));
        Assert.Equal(105.0 / 120, m.Oee.Availability, 3);
        Assert.Equal(60.0 / 62, m.Oee.Quality, 3);
        Assert.Equal("TOOL_CHANGE", Assert.Single(kpi.DowntimePareto).ReasonCode);
    }

    [Fact]
    public async Task Watchdog_marks_silent_machines_offline_and_opens_downtime()
    {
        await SignalAsync(new("L2-CNC-01", "state", "Running", TimestampUtc: Start));
        _clock.Advance(TimeSpan.FromMinutes(2));

        await using (var db = _db.CreateContext())
            Assert.Equal(1, await Telemetry(db).MarkStaleMachinesOfflineAsync(default));

        await using var check = _db.CreateContext();
        Assert.Equal(MachineState.Offline, (await check.Machines.SingleAsync(m => m.Code == "L2-CNC-01")).State);
        Assert.Equal("NO_HEARTBEAT", (await check.DowntimeEvents.SingleAsync()).ReasonCode);
    }

    [Fact]
    public async Task Concurrent_edits_to_the_same_balance_are_detected()
    {
        await using var a = _db.CreateContext();
        await using var b = _db.CreateContext();
        var balanceA = await a.InventoryBalances.SingleAsync(x => x.LotNumber == "HEAT-88231");
        var balanceB = await b.InventoryBalances.SingleAsync(x => x.LotNumber == "HEAT-88231");

        balanceA.Remove(100);
        await a.SaveChangesAsync();

        balanceB.Remove(750); // based on a stale read of 800
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }
}
