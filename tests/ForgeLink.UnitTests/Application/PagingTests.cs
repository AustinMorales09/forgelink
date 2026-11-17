using ForgeLink.Application.Abstractions;
using ForgeLink.Application.Common;
using ForgeLink.Application.Inventory;
using ForgeLink.Domain;
using ForgeLink.Domain.Inventory;
using Microsoft.Extensions.Options;

namespace ForgeLink.UnitTests.Application;

public sealed class PagingTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    private readonly FakeClock _clock = new(Start);
    private readonly TestDb _db;

    public PagingTests() => _db = new TestDb(_clock, seed: false);

    public void Dispose() => _db.Dispose();

    private InventoryService Inventory(IAppDbContext db) =>
        new(db, new TestUser("tester"), _clock, new NullShopFloorNotifier(), Options.Create(new ForgeLinkOptions()));

    private async Task AddAsync(int count, DateTime at, string sku = "BRKT-100")
    {
        await using var db = _db.CreateContext();
        for (var i = 0; i < count; i++)
            db.InventoryTransactions.Add(InventoryTransaction.Create(InventoryTransactionType.Adjustment, sku, "", 1, null, "STORES", $"REF-{at:HHmm}-{i}", "FOUND", "tester", at));
        await db.SaveChangesAsync();
    }

    private async Task<List<Guid>> WalkAsync(int limit, string? sku = null)
    {
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            await using var db = _db.CreateContext();
            var page = await Inventory(db).LedgerAsync(sku, limit, cursor, CancellationToken.None);
            Assert.True(page.Items.Count <= limit);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            Assert.True(++pages < 100, "pagination did not terminate");
        }
        while (cursor is not null);
        return seen;
    }

    [Fact]
    public async Task Walks_every_row_exactly_once_newest_first_even_with_identical_timestamps()
    {
        await AddAsync(2, Start.AddMinutes(30));
        await AddAsync(7, Start.AddMinutes(20)); // a burst that straddles several page boundaries
        await AddAsync(3, Start.AddMinutes(10));

        foreach (var limit in new[] { 1, 2, 3, 5, 12, 50 })
        {
            var ids = await WalkAsync(limit);
            Assert.Equal(12, ids.Count);
            Assert.Equal(12, ids.Distinct().Count());
        }

        await using var db = _db.CreateContext();
        var all = await Inventory(db).LedgerAsync(null, 50, null, CancellationToken.None);
        Assert.Equal(all.Items.OrderByDescending(i => i.OccurredAtUtc).Select(i => i.OccurredAtUtc), all.Items.Select(i => i.OccurredAtUtc));
        Assert.Null(all.NextCursor);
    }

    [Fact]
    public async Task Rows_added_while_paging_do_not_shift_later_pages()
    {
        await AddAsync(6, Start.AddMinutes(5));
        await using (var db = _db.CreateContext())
        {
            var first = await Inventory(db).LedgerAsync(null, 4, null, CancellationToken.None);
            await AddAsync(10, Start.AddMinutes(50)); // newer rows arrive between page requests
            var second = await Inventory(db).LedgerAsync(null, 4, first.NextCursor, CancellationToken.None);
            Assert.Equal(2, second.Items.Count);
            Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
            Assert.Null(second.NextCursor);
        }
    }

    [Fact]
    public async Task Filters_by_sku_and_clamps_limit()
    {
        await AddAsync(3, Start, "BRKT-100");
        await AddAsync(2, Start, "PIN-7");
        Assert.Equal(2, (await WalkAsync(1, "pin-7")).Count);

        await using var db = _db.CreateContext();
        var page = await Inventory(db).LedgerAsync(null, 10_000, null, CancellationToken.None);
        Assert.Equal(5, page.Items.Count);
        Assert.Equal(TimeCursor.MaxLimit, TimeCursor.ClampLimit(10_000));
        Assert.Equal(1, TimeCursor.ClampLimit(0));
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("MTIzOmFiYw")] // "123:abc"
    [InlineData("MTIzOjA")]    // "123:0" (count must be positive)
    public void Rejects_tampered_cursors_with_a_validation_error(string cursor)
    {
        var ex = Assert.Throws<DomainException>(() => TimeCursor.Decode(cursor));
        Assert.Equal("validation", ex.Code);
    }

    [Fact]
    public void Cursor_round_trips()
    {
        var c = new TimeCursor(Start.AddTicks(12345), 3);
        Assert.Equal(c, TimeCursor.Decode(c.Encode()));
        Assert.Null(TimeCursor.Decode(null));
    }
}
