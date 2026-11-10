using ForgeLink.Domain;
using ForgeLink.Domain.Equipment;
using ForgeLink.Domain.Integration;
using ForgeLink.Domain.Inventory;
using ForgeLink.Domain.Production;
using ForgeLink.Domain.Quality;

namespace ForgeLink.UnitTests.Domain;

public class WorkOrderTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

    private static WorkOrder NewOrder(decimal qty = 100) => new("wo-1", "BRK-1001", qty, "L1", Now.AddDays(1));

    [Fact]
    public void Number_is_normalised_and_order_starts_released()
    {
        var wo = NewOrder();
        Assert.Equal("WO-1", wo.Number);
        Assert.Equal(WorkOrderStatus.Released, wo.Status);
    }

    [Fact]
    public void Cannot_report_production_before_start()
    {
        var ex = Assert.Throws<DomainException>(() => NewOrder().ReportProduction(5, 0));
        Assert.Equal("workorder.not-running", ex.Code);
    }

    [Fact]
    public void Start_is_idempotent_on_the_same_machine_but_rejects_a_second_machine()
    {
        var wo = NewOrder();
        wo.Start("L1-CNC-01", Now);
        wo.Start("L1-CNC-01", Now.AddMinutes(1)); // retried request: no error
        Assert.Equal(Now, wo.StartedAtUtc);

        var ex = Assert.Throws<DomainException>(() => wo.Start("L1-CNC-02", Now));
        Assert.Equal("workorder.running-elsewhere", ex.Code);
    }

    [Fact]
    public void Overrun_up_to_tolerance_is_allowed_beyond_is_rejected()
    {
        var wo = NewOrder(100);
        wo.Start("M1", Now);
        wo.ReportProduction(110, 0); // exactly +10 %
        Assert.Equal(0, wo.QuantityRemaining);

        var ex = Assert.Throws<DomainException>(() => wo.ReportProduction(1, 0));
        Assert.Equal("workorder.overrun", ex.Code);
        Assert.Equal(110, wo.QuantityCompleted); // unchanged after rejection
    }

    [Fact]
    public void Zero_or_negative_reports_are_rejected()
    {
        var wo = NewOrder();
        wo.Start("M1", Now);
        Assert.Throws<DomainException>(() => wo.ReportProduction(0, 0));
        Assert.Throws<DomainException>(() => wo.ReportProduction(-1, 0));
    }

    [Fact]
    public void Erp_cancel_while_running_is_a_conflict_not_a_silent_change()
    {
        var wo = NewOrder();
        wo.Start("M1", Now);
        Assert.False(wo.ApplyErpUpdate(100, Now.AddDays(2), cancelled: true));
        Assert.Equal(WorkOrderStatus.InProgress, wo.Status);

        var idle = NewOrder();
        Assert.True(idle.ApplyErpUpdate(100, Now.AddDays(2), cancelled: true));
        Assert.Equal(WorkOrderStatus.Cancelled, idle.Status);
    }

    [Fact]
    public void Complete_clears_machine_and_stamps_time()
    {
        var wo = NewOrder();
        wo.Start("M1", Now);
        wo.Complete(Now.AddHours(2));
        Assert.Equal(WorkOrderStatus.Completed, wo.Status);
        Assert.Null(wo.CurrentMachine);
        Assert.Equal(Now.AddHours(2), wo.CompletedAtUtc);
        Assert.Throws<DomainException>(() => wo.Start("M1", Now));
    }
}

public class OeeTests
{
    [Fact]
    public void Classic_textbook_example()
    {
        // 8 h planned, 1 h down, 60 s ideal cycle, 400 made, 380 good.
        var r = OeeCalculator.Calculate(TimeSpan.FromHours(8), TimeSpan.FromHours(1), 400, 380, 60);
        Assert.Equal(0.875, r.Availability, 3);
        Assert.Equal(400 * 60 / (7 * 3600.0), r.Performance, 3);
        Assert.Equal(0.95, r.Quality, 3);
        Assert.Equal(r.Availability * r.Performance * r.Quality, r.Oee, 6);
    }

    [Fact]
    public void Values_are_clamped_and_never_nan()
    {
        Assert.Equal(OeeResult.Zero, OeeCalculator.Calculate(TimeSpan.Zero, TimeSpan.Zero, 10, 10, 30));
        var allDown = OeeCalculator.Calculate(TimeSpan.FromHours(1), TimeSpan.FromHours(2), 0, 0, 30);
        Assert.Equal(0, allDown.Availability);
        Assert.Equal(0, allDown.Oee);
        var tooFast = OeeCalculator.Calculate(TimeSpan.FromHours(1), TimeSpan.Zero, 1000, 1000, 30);
        Assert.Equal(1, tooFast.Performance); // bad ideal-cycle master data must not show >100 %
    }

    [Fact]
    public void Downtime_overlap_is_clipped_to_the_window()
    {
        var from = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var e = new DowntimeEvent("M1", from.AddMinutes(-30), "JAM");
        e.Close(from.AddMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(15), e.OverlapWith(from, from.AddHours(1)));

        var open = new DowntimeEvent("M1", from.AddMinutes(50), "JAM");
        Assert.Equal(TimeSpan.FromMinutes(10), open.OverlapWith(from, from.AddHours(1)));
    }
}

public class InventoryDomainTests
{
    [Fact]
    public void Balance_cannot_go_negative()
    {
        var b = new InventoryBalance("sku", "loc", "lot");
        b.Add(5);
        var ex = Assert.Throws<DomainException>(() => b.Remove(6));
        Assert.Equal("inventory.insufficient", ex.Code);
        Assert.Equal(5, b.Quantity);
    }

    [Fact]
    public void Held_lot_can_only_go_to_quarantine()
    {
        var lot = new Lot("L-1", "SKU", null, DateTime.UtcNow);
        lot.Hold("Bore oversize");
        Assert.Equal("lot.on-hold", Assert.Throws<DomainException>(() => lot.EnsureUsable("L1-WIP")).Code);
        lot.EnsureUsable("QA-HOLD"); // allowed
        lot.Release();
        lot.EnsureUsable("L1-WIP");
    }

    [Theory]
    [InlineData(100, 102, 2, true)]
    [InlineData(100, 97.9, 2, false)]
    [InlineData(0, 0, 2, true)]
    [InlineData(0, 1, 2, false)]
    public void Cycle_count_tolerance(decimal system, decimal counted, decimal tolerance, bool within) =>
        Assert.Equal(within, CycleCount.IsWithinTolerance(system, counted, tolerance));

    [Fact]
    public void Counter_cannot_approve_own_variance()
    {
        var cc = CycleCount.Record("SKU", "LOC", "", 100, 80, "alice", 2, DateTime.UtcNow);
        Assert.Equal(CycleCountStatus.PendingApproval, cc.Status);
        Assert.Equal("cyclecount.self-approval", Assert.Throws<DomainException>(() => cc.Approve("ALICE", DateTime.UtcNow)).Code);
        cc.Approve("bob", DateTime.UtcNow);
        Assert.Equal(CycleCountStatus.Posted, cc.Status);
        Assert.Equal(-20, cc.Variance);
    }

    [Fact]
    public void Quality_check_evaluates_limits_inclusively()
    {
        var pass = QualityCheck.Record("WO", "LOT", "Bore", 42, 41.975m, 42.025m, 42.025m, "qa", DateTime.UtcNow);
        var fail = QualityCheck.Record("WO", "LOT", "Bore", 42, 41.975m, 42.025m, 42.026m, "qa", DateTime.UtcNow);
        Assert.Equal(QualityResult.Pass, pass.Result);
        Assert.Equal(QualityResult.Fail, fail.Result);
        Assert.Throws<DomainException>(() => QualityCheck.Record("WO", "LOT", "Bore", 50, 41, 42, 41.5m, "qa", DateTime.UtcNow));
    }
}

public class OutboxDomainTests
{
    [Fact]
    public void Backoff_doubles_and_caps_without_jitter()
    {
        var b = TimeSpan.FromSeconds(2);
        var max = TimeSpan.FromSeconds(60);
        Assert.Equal(2, RetryBackoff.Compute(1, b, max, 0).TotalSeconds);
        Assert.Equal(4, RetryBackoff.Compute(2, b, max, 0).TotalSeconds);
        Assert.Equal(16, RetryBackoff.Compute(4, b, max, 0).TotalSeconds);
        Assert.Equal(60, RetryBackoff.Compute(12, b, max, 0).TotalSeconds);
    }

    [Fact]
    public void Jitter_stays_within_bounds()
    {
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var d = RetryBackoff.Compute(3, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), 0.2, rng).TotalSeconds;
            Assert.InRange(d, 6.4, 9.6);
        }
    }

    [Fact]
    public void Sent_message_cannot_be_cancelled_or_requeued()
    {
        var m = new OutboxMessage("t", "p", "{}", "k", DateTime.UtcNow);
        m.MarkSent("ERP-1", DateTime.UtcNow);
        Assert.Throws<DomainException>(() => m.Cancel("oops", "bob"));
        Assert.Throws<DomainException>(() => m.Requeue(DateTime.UtcNow));
    }

    [Fact]
    public void Machine_ignores_out_of_order_state_messages()
    {
        var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var m = new Machine("M1", "L1", "test", 30);
        Assert.True(m.ApplyState(MachineState.Running, null, t0.AddMinutes(5)));
        Assert.False(m.ApplyState(MachineState.Down, "JAM", t0.AddMinutes(1))); // late delivery
        Assert.Equal(MachineState.Running, m.State);
        Assert.Equal(t0.AddMinutes(5), m.LastHeartbeatUtc);
    }
}
