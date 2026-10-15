using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using BusinessData.Core;
using BusinessData.Server;
using Microsoft.Data.Sqlite;

namespace BusinessData.Tests;

// Literal facts are the approved independent fixture contract, never read from the seed.
public sealed class IndependentContractTests
{
    [Theory]
    [InlineData("ORD-1001", "CUST-001", "Avery Morgan", "delivered", 12990)]
    [InlineData("ORD-1002", "CUST-001", "Avery Morgan", "shipped", 7990)]
    [InlineData("ORD-1003", "CUST-002", "Jordan Lee", "delivered", 24900)]
    [InlineData("ORD-1004", "CUST-003", "Taylor Quinn", "cancelled", 4500)]
    public async Task ExactFactsSurviveOfficialSdkSerialization(string id, string customer, string name, string status, long cents)
    {
        await using var session = await PipeSession.Start();
        using var document = JsonDocument.Parse(PipeSession.Text(await session.Call("get_order", new() { ["orderId"] = id })));
        var order = document.RootElement;
        Assert.Equal(id, order.GetProperty("id").GetString());
        Assert.Equal(customer, order.GetProperty("customerId").GetString());
        Assert.Equal(name, order.GetProperty("customerName").GetString());
        Assert.Equal(status, order.GetProperty("status").GetString());
        Assert.Equal(cents, order.GetProperty("totalCents").GetInt64());
        Assert.Equal("USD", order.GetProperty("currency").GetString());
        using var statusDocument = JsonDocument.Parse(PipeSession.Text(await session.Call("get_order_status", new() { ["orderId"] = id })));
        Assert.Equal(status, statusDocument.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void SearchAndHistoryHaveExactIndependentMembershipAndBounds()
    {
        using var db = new TestDatabase();
        var history = db.Store.GetCustomerHistory("CUST-001");
        Assert.Equal(new[] { "ORD-1001", "ORD-1002" }, history.Orders.Select(o => o.Id).Order().ToArray());
        Assert.Equal(20980, history.Orders.Sum(o => o.TotalCents));
        Assert.Equal(new[] { "ORD-1001", "ORD-1002" }, db.Store.SearchOrders("ORD-100", 2).Select(o => o.Id).ToArray());
        Assert.Equal(4, db.Store.SearchOrders("ORD-100", 20).Count);
        Assert.Empty(db.Store.SearchOrders(new string('x', 80)));
        foreach (var query in new[] { "", " ", new string('x', 81) })
            Assert.Equal("invalid_argument", Assert.Throws<BusinessException>(() => db.Store.SearchOrders(query)).Code);
        foreach (var limit in new[] { -1, 0, 21 })
            Assert.Equal("invalid_argument", Assert.Throws<BusinessException>(() => db.Store.SearchOrders("ORD", limit)).Code);
    }

    [Theory]
    [InlineData(599999, true)]
    [InlineData(600000, false)]
    [InlineData(600001, false)]
    public void ExactExpiryBoundaryAndImmutableBinding(int milliseconds, bool valid)
    {
        using var db = new TestDatabase();
        db.Clock.Now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var intent = db.Store.PrepareReturn("writer:A", "ORD-1001", "damaged", "prepare-boundary");
        Assert.Equal(db.Clock.Now.AddSeconds(600), DateTimeOffset.Parse(intent.ExpiresAt));
        db.Store.ApproveReturn(intent.Id);
        db.Clock.Now += TimeSpan.FromMilliseconds(milliseconds);
        if (valid) Assert.Equal(intent.Id, db.Store.RequestReturn("writer:A", intent.Id, "submit-boundary").IntentId);
        else Assert.Equal("expired_confirmation", Assert.Throws<BusinessException>(() => db.Store.RequestReturn("writer:A", intent.Id, "submit-boundary")).Code);
        using var connection = new SqliteConnection("Data Source=" + db.Path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT actor,order_id,reason,(SELECT count(*) FROM returns) FROM return_intents";
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal("writer:A", row.GetString(0));
        Assert.Equal("ORD-1001", row.GetString(1));
        Assert.Equal("damaged", row.GetString(2));
        Assert.Equal(valid ? 1L : 0L, row.GetInt64(3));
        Assert.False(row.Read());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EightConcurrentSubmissionsHaveOneIndependentDurableRow(bool identicalKey)
    {
        using var db = new TestDatabase();
        var intent = db.Store.PrepareReturn("writer:A", "ORD-1001", "damaged", "prepare-race-key");
        db.Store.ApproveReturn(intent.Id);
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            start.Wait();
            try { return (Ticket: new BusinessStore(db.Path, db.Clock).RequestReturn("writer:A", intent.Id, identicalKey ? "submit-race-key" : $"submit-race-{i:D3}"), Code: "ok"); }
            catch (BusinessException error) { return (Ticket: (ReturnTicket?)null, Code: error.Code); }
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);
        var successes = results.Where(r => r.Ticket != null).ToArray();
        Assert.Equal(identicalKey ? 8 : 1, successes.Length);
        Assert.Single(successes.Select(r => r.Ticket!.Id).Distinct());
        Assert.All(results.Where(r => r.Ticket == null), r => Assert.Equal("already_consumed", r.Code));
        using var connection = new SqliteConnection("Data Source=" + db.Path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,actor,order_id,intent_id FROM returns";
        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(successes[0].Ticket!.Id, rows.GetString(0));
        Assert.Equal("writer:A", rows.GetString(1));
        Assert.Equal("ORD-1001", rows.GetString(2));
        Assert.Equal(intent.Id, rows.GetString(3));
        Assert.False(rows.Read());
    }

    [Fact]
    public async Task ActivityAndMetricsAreCorrelatedRedactedAndHaveSafeOutcomes()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var measurements = new ConcurrentQueue<(string Name, double Value)>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, instance) => { if (instrument.Meter.Name == Telemetry.SourceName) instance.EnableMeasurementEvents(instrument); };
        meter.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurements.Enqueue((instrument.Name, value)));
        meter.SetMeasurementEventCallback<double>((instrument, value, _, _) => measurements.Enqueue((instrument.Name, value)));
        meter.Start();
        await using var session = await PipeSession.Start();
        await session.Call("search_orders", new() { ["query"] = "QUERY-CANARY-DO-NOT-LOG" });
        var failed = await session.Call("get_order", new() { ["orderId"] = "ORD-9999" });
        using var error = JsonDocument.Parse(PipeSession.Text(failed));
        var correlation = error.RootElement.GetProperty("correlationId").GetString();
        var own = activities.Where(a => session.Database.Store.ReadAudit().Any(row => row.Id == (string?)a.GetTagItem("correlation.id"))).ToArray();
        Assert.Equal(2, own.Length);
        Assert.Contains(own, a => a.Status == ActivityStatusCode.Ok && (string?)a.GetTagItem("outcome.code") == "ok");
        Assert.Contains(own, a => a.Status == ActivityStatusCode.Error && (string?)a.GetTagItem("correlation.id") == correlation);
        Assert.All(own, a => { Assert.True(a.Duration >= TimeSpan.Zero); Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(a.TagObjects)); });
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(session.Database.Store.ReadAudit()));
        Assert.Contains(measurements, m => m.Name == "mcp.tool.calls" && m.Value == 1);
        Assert.Contains(measurements, m => m.Name == "mcp.tool.duration" && double.IsFinite(m.Value) && m.Value >= 0);
    }
}
