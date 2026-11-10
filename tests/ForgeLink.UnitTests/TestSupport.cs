using ForgeLink.Application.Abstractions;
using ForgeLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ForgeLink.UnitTests;

public sealed class FakeClock(DateTime startUtc) : TimeProvider
{
    public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class TestUser(string name, params string[] roles) : ICurrentUser
{
    public string Name { get; set; } = name;

    public bool IsInRole(string role) => roles.Contains(role);
}

/// <summary>
/// Real relational database (SQLite in-memory) rather than the EF in-memory provider, so constraints,
/// transactions, converters and query translation are exercised exactly as in production.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb(FakeClock clock, bool seed = true)
    {
        Clock = clock;
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        if (seed) DemoDataSeeder.SeedAsync(db, clock.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
    }

    public FakeClock Clock { get; }

    public ForgeLinkDbContext CreateContext(ICurrentUser? user = null) =>
        new(new DbContextOptionsBuilder<ForgeLinkDbContext>().UseSqlite(_connection).Options, user ?? new TestUser("tester"), Clock);

    public void Dispose() => _connection.Dispose();
}
