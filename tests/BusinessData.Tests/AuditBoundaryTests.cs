using System.Text.Json;
using BusinessData.Core;

namespace BusinessData.Tests;

public sealed class AuditBoundaryTests
{
    private sealed class FaultingAudit(BusinessStore store, bool failStart) : IAuditSink
    {
        public string BeginAudit(string actor, string tool, string safeArguments, string? correlationId = null)
        {
            if (failStart) throw new IOException("private storage canary");
            return store.BeginAudit(actor, tool, safeArguments, correlationId);
        }

        public void FinishAudit(string id, string outcome, string resultCode, long durationMs, string? resultMetadata = null) =>
            throw new IOException("private completion canary");
    }

    [Fact]
    public async Task StartFailureClosesWriteBeforeAnyBusinessMutation()
    {
        await using var session = await PipeSession.Start(writes: true, auditFactory: store => new FaultingAudit(store, true));
        var response = await session.Call("prepare_return", new()
            { ["orderId"] = "ORD-1001", ["reason"] = "damaged", ["idempotencyKey"] = "audit-start-key" });
        var error = JsonDocument.Parse(PipeSession.Text(response)).RootElement;
        Assert.True(response.IsError);
        Assert.Equal("audit_unavailable", error.GetProperty("code").GetString());
        Assert.Matches("^[a-f0-9]{32}$", error.GetProperty("correlationId").GetString()!);
        Assert.Empty(session.Database.Store.ReadAudit());
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + session.Database.Path);
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT count(*) FROM return_intents) + (SELECT count(*) FROM returns)";
        Assert.Equal(0L, count.ExecuteScalar());
        Assert.DoesNotContain("canary", PipeSession.Text(response));
    }

    [Theory]
    [InlineData(false, "get_order", "audit_unavailable", "ok")]
    [InlineData(false, "prepare_return", "audit_unavailable", "forbidden")]
    [InlineData(true, "prepare_return", "outcome_unknown", "ok")]
    public async Task CompletionFailurePreservesSafeOriginalOutcome(bool writer, string tool, string expected, string original)
    {
        await using var session = await PipeSession.Start(writes: writer, auditFactory: store => new FaultingAudit(store, false));
        var args = tool == "get_order"
            ? new Dictionary<string, object?> { ["orderId"] = "ORD-1001" }
            : new Dictionary<string, object?> { ["orderId"] = "ORD-1001", ["reason"] = "damaged", ["idempotencyKey"] = "audit-prepare-key" };
        var response = await session.Call(tool, args);
        var error = JsonDocument.Parse(PipeSession.Text(response)).RootElement;
        Assert.True(response.IsError);
        Assert.Equal(expected, error.GetProperty("code").GetString());
        Assert.Equal(original, error.GetProperty("originalCode").GetString());
        var started = Assert.Single(session.Database.Store.ReadAudit());
        Assert.Equal("started", started.Outcome);
        Assert.Equal(started.Id, error.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("canary", PipeSession.Text(response));
        if (writer)
        {
            // Durable preparation survived uncertain completion; the original key recovers its identity.
            var intent = session.Database.Store.PrepareReturn("test:local", "ORD-1001", "damaged", "audit-prepare-key");
            Assert.Matches("^RI-[a-f0-9]{32}$", intent.Id);
            Assert.Equal(intent.Id, session.Database.Store.PrepareReturn("test:local", "ORD-1001", "damaged", "audit-prepare-key").Id);
        }
    }
}
