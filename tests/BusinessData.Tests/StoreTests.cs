using BusinessData.Core;

namespace BusinessData.Tests;

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class TestDatabase : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "business-mcp-tests-" + Guid.NewGuid().ToString("N"));
    public string Path { get; }
    public TestClock Clock { get; } = new();
    public BusinessStore Store { get; }
    public TestDatabase()
    {
        Path = System.IO.Path.Combine(directory, "test.db");
        Store = new(Path, Clock);
        Store.Initialize();
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}

public sealed class StoreTests
{
    [Fact]
    public void SeedIsRepeatableAndHistoryIsCustomerBound()
    {
        using var db = new TestDatabase();
        db.Store.Initialize();
        Assert.Equal(4, db.Store.SearchOrders("ORD-").Count);
        var history = db.Store.GetCustomerHistory("CUST-001");
        Assert.Equal("Avery Morgan", history.Customer.Name);
        Assert.Equal(2, history.Orders.Count);
        Assert.All(history.Orders, o => Assert.Equal("CUST-001", o.CustomerId));
        Assert.Equal("shipped", db.Store.GetOrder("ORD-1002").Status);
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public void SearchDoesNotInterpretSqlOrWildcardInput(string input)
    {
        using var db = new TestDatabase();
        Assert.Empty(db.Store.SearchOrders(input));
        Assert.Equal(4, db.Store.SearchOrders("ORD-").Count);
    }

    [Fact]
    public void BoundedQueriesAndNotFoundHaveStableErrors()
    {
        using var db = new TestDatabase();
        Assert.Single(db.Store.SearchOrders("ORD-", 1));
        AssertCode("invalid_argument", () => db.Store.SearchOrders("", 10));
        AssertCode("invalid_argument", () => db.Store.SearchOrders("ORD-", 21));
        AssertCode("invalid_argument", () => db.Store.GetOrder("'; DROP TABLE orders;"));
        AssertCode("not_found", () => db.Store.GetOrder("ORD-9999"));
        AssertCode("not_found", () => db.Store.GetCustomerHistory("CUST-999"));
    }

    [Fact]
    public void ReturnRequiresApprovalAndTheOriginalPrincipal()
    {
        using var db = new TestDatabase();
        var intent = db.Store.PrepareReturn("writer", "ORD-1001", "damaged", "prepare-0001");
        AssertCode("confirmation_required", () => db.Store.RequestReturn("writer", intent.Id, "submit-0001"));
        Assert.Empty(db.Store.GetCustomerHistory("CUST-001").Returns);
        db.Store.ApproveReturn(intent.Id);
        AssertCode("forbidden", () => db.Store.RequestReturn("other", intent.Id, "submit-0001"));
        var ticket = db.Store.RequestReturn("writer", intent.Id, "submit-0001");
        Assert.Equal("requested", ticket.Status);
        Assert.Single(db.Store.GetCustomerHistory("CUST-001").Returns);
        Assert.NotNull(db.Store.GetIntent(intent.Id).ConsumedAt);
    }

    [Fact]
    public void IdempotencyIsDurableAndDetectsChangedArguments()
    {
        using var db = new TestDatabase();
        var intent = db.Store.PrepareReturn("writer", "ORD-1001", "damaged", "prepare-0001");
        var restarted = new BusinessStore(db.Path, db.Clock);
        Assert.Equal(intent, restarted.PrepareReturn("writer", "ORD-1001", "damaged", "prepare-0001"));
        AssertCode("idempotency_conflict", () => restarted.PrepareReturn("writer", "ORD-1003", "damaged", "prepare-0001"));
        restarted.ApproveReturn(intent.Id);
        var ticket = restarted.RequestReturn("writer", intent.Id, "submit-0001");
        db.Clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(ticket, db.Store.RequestReturn("writer", intent.Id, "submit-0001"));
        AssertCode("idempotency_conflict", () => db.Store.RequestReturn("writer", "RI-other", "submit-0001"));
    }

    [Fact]
    public void ApprovalExpiresAndAnIntentCanOnlyBeConsumedOnce()
    {
        using var db = new TestDatabase();
        var first = db.Store.PrepareReturn("writer", "ORD-1001", "damaged", "prepare-0001");
        db.Store.ApproveReturn(first.Id);
        db.Clock.Now += TimeSpan.FromMinutes(10);
        AssertCode("expired_confirmation", () => db.Store.RequestReturn("writer", first.Id, "submit-0001"));
        AssertCode("expired_confirmation", () => db.Store.ApproveReturn(first.Id));
        var fresh = db.Store.PrepareReturn("writer", "ORD-1001", "damaged", "prepare-0002");
        db.Store.ApproveReturn(fresh.Id);
        db.Store.RequestReturn("writer", fresh.Id, "submit-0001");
        AssertCode("already_consumed", () => db.Store.RequestReturn("writer", fresh.Id, "submit-0002"));
    }

    [Theory]
    [InlineData("ORD-1002", "damaged", "ineligible_order")]
    [InlineData("ORD-1004", "damaged", "ineligible_order")]
    [InlineData("ORD-1001", "arbitrary text", "invalid_argument")]
    public void InvalidReturnsHaveNoTicketSideEffect(string order, string reason, string code)
    {
        using var db = new TestDatabase();
        AssertCode(code, () => db.Store.PrepareReturn("writer", order, reason, "prepare-0001"));
        Assert.Empty(db.Store.GetCustomerHistory(db.Store.GetOrder(order).CustomerId).Returns);
    }

    [Fact]
    public async Task ConcurrentRetriesCreateExactlyOneTicket()
    {
        using var db = new TestDatabase();
        var intent = db.Store.PrepareReturn("writer", "ORD-1001", "damaged", "prepare-0001");
        db.Store.ApproveReturn(intent.Id);
        var tickets = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            new BusinessStore(db.Path, db.Clock).RequestReturn("writer", intent.Id, "submit-0001"))));
        Assert.Single(tickets.Select(t => t.Id).Distinct());
        Assert.Single(db.Store.GetCustomerHistory("CUST-001").Returns);
    }

    [Fact]
    public void DifferentIntentsCannotReturnTheSameOrderTwice()
    {
        using var db = new TestDatabase();
        var a = db.Store.PrepareReturn("writer", "ORD-1001", "damaged", "prepare-0001");
        var b = db.Store.PrepareReturn("writer", "ORD-1001", "wrong_item", "prepare-0002");
        db.Store.ApproveReturn(a.Id);
        db.Store.ApproveReturn(b.Id);
        db.Store.RequestReturn("writer", a.Id, "submit-0001");
        AssertCode("already_returned", () => db.Store.RequestReturn("writer", b.Id, "submit-0002"));
        Assert.Null(db.Store.GetIntent(b.Id).ConsumedAt);
        Assert.Single(db.Store.GetCustomerHistory("CUST-001").Returns);
    }

    private static void AssertCode(string code, Action action) => Assert.Equal(code, Assert.Throws<BusinessException>(action).Code);
}
