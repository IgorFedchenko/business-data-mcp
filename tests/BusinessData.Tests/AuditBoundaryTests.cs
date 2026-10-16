using System.Text.Json;
using BusinessData.Core;
using Microsoft.Extensions.DependencyInjection;

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

public sealed class IndependentAuditCommitTests
{
    private sealed class FailCompletion(BusinessStore store) : IAuditSink
    {
        public string BeginAudit(string actor, string tool, string safeArguments, string? correlationId = null) => store.BeginAudit(actor, tool, safeArguments, correlationId);
        public void FinishAudit(string id, string outcome, string resultCode, long durationMs, string? resultMetadata = null) => throw new IOException("COMPLETION-PRIVATE-CANARY");
    }

    [Fact]
    public async Task SubmitCommitWithFailedAuditCanBeRecoveredAfterStoreRestart()
    {
        await using var session = await PipeSession.Start(writes: true, auditFactory: store => new FailCompletion(store));
        var intent = session.Database.Store.PrepareReturn("test:local", "ORD-1001", "damaged", "prepare-uncertain");
        session.Database.Store.ApproveReturn(intent.Id);
        var response = await session.Call("request_return", new() { ["intentId"] = intent.Id, ["idempotencyKey"] = "submit-uncertain" });
        using var error = JsonDocument.Parse(PipeSession.Text(response));
        Assert.True(response.IsError);
        Assert.Equal("outcome_unknown", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("ok", error.RootElement.GetProperty("originalCode").GetString());
        Assert.Contains("original idempotency key", error.RootElement.GetProperty("message").GetString());
        var started = Assert.Single(session.Database.Store.ReadAudit());
        Assert.Equal("started", started.Outcome);
        Assert.Equal(started.Id, error.RootElement.GetProperty("correlationId").GetString());
        var restarted = new BusinessStore(session.Database.Path, session.Database.Clock);
        var recovered = restarted.RequestReturn("test:local", intent.Id, "submit-uncertain");
        Assert.Equal(recovered, restarted.RequestReturn("test:local", intent.Id, "submit-uncertain"));
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + session.Database.Path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,intent_id,(SELECT consumed_at FROM return_intents WHERE id=returns.intent_id) FROM returns";
        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(recovered.Id, rows.GetString(0));
        Assert.Equal(intent.Id, rows.GetString(1));
        Assert.False(rows.IsDBNull(2));
        Assert.False(rows.Read());
        Assert.DoesNotContain("PRIVATE", PipeSession.Text(response));
    }

    [Fact]
    public async Task InvalidWriteWithFailedCompletionIsNotReportedAsPossiblyCommitted()
    {
        await using var session = await PipeSession.Start(writes: true, auditFactory: store => new FailCompletion(store));
        var response = await session.Call("prepare_return", new() { ["orderId"] = "ORD-1001", ["reason"] = "PRIVATE-REASON", ["idempotencyKey"] = "prepare-invalid-key" });
        using var error = JsonDocument.Parse(PipeSession.Text(response));
        Assert.Equal("audit_unavailable", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("invalid_argument", error.RootElement.GetProperty("originalCode").GetString());
        Assert.DoesNotContain("may have committed", PipeSession.Text(response));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(session.Database.Store.ReadAudit()));
    }
}

public sealed class IndependentStorageFaultTests
{
    [Fact]
    public async Task StorageFailureBeforeCommitRollsBackTicketAndConsumptionAndIsRedacted()
    {
        await using var session = await PipeSession.Start(writes: true);
        var intent = session.Database.Store.PrepareReturn("test:local", "ORD-1001", "damaged", "prepare-storage-fault");
        session.Database.Store.ApproveReturn(intent.Id);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + session.Database.Path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Fixture-only SQLite trigger, never an environment/HTTP/MCP production switch.
            command.CommandText = "CREATE TRIGGER fail_ticket BEFORE INSERT ON returns BEGIN SELECT RAISE(ABORT,'PRIVATE-SQL-CANARY'); END";
            command.ExecuteNonQuery();
        }
        var response = await session.Call("request_return", new() { ["intentId"] = intent.Id, ["idempotencyKey"] = "submit-storage-fault" });
        Assert.True(response.IsError);
        Assert.DoesNotContain("PRIVATE", PipeSession.Text(response));
        var row = Assert.Single(session.Database.Store.ReadAudit());
        Assert.Equal("error", row.Outcome);
        Assert.NotEqual("ok", row.ResultCode);
        Assert.NotNull(row.DurationMs);
        Assert.True(row.DurationMs >= 0);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + session.Database.Path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT count(*) FROM returns),(SELECT count(*) FROM return_intents WHERE consumed_at IS NOT NULL)";
            using var rows = command.ExecuteReader();
            Assert.True(rows.Read());
            Assert.Equal(0, rows.GetInt64(0));
            Assert.Equal(0, rows.GetInt64(1));
        }
    }
}


public sealed class InternalHandlerBoundaryTests
{
    [Theory]
    [InlineData(false, "internal_error")]
    [InlineData(true, "cancelled")]
    public async Task InternalHandlerFaultAndCancellationAreSafeAndAudited(bool cancel, string expectedCode)
    {
        // Test composition replaces only the final handler; the real SDK transport and security filter still execute.
        await using var session = await PipeSession.Start(configure: mcp => mcp.WithRequestFilters(filters =>
            filters.AddCallToolFilter(_ => (_, _) =>
            {
                if (cancel) throw new OperationCanceledException("PRIVATE-CANCEL-CANARY");
                throw new IOException("PRIVATE-FAULT-CANARY");
            })));
        var response = await session.Call("get_order", new() { ["orderId"] = "ORD-1001" });
        Assert.True(response.IsError);
        using var error = JsonDocument.Parse(PipeSession.Text(response));
        Assert.Equal(expectedCode, error.RootElement.GetProperty("code").GetString());
        var audit = Assert.Single(session.Database.Store.ReadAudit());
        Assert.Equal(error.RootElement.GetProperty("correlationId").GetString(), audit.Id);
        Assert.Equal(expectedCode, audit.ResultCode);
        Assert.Equal("error", audit.Outcome);
        Assert.NotNull(audit.DurationMs);
        Assert.DoesNotContain("PRIVATE", PipeSession.Text(response));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(audit));
    }
}

public sealed class IndependentAuditIdentityTests
{
    [Fact]
    public async Task CreatedAndReplayedCallsRetainSameSafeIdentityWithoutBusinessPayload()
    {
        await using var session = await PipeSession.Start(writes: true);
        var prepareArgs = new Dictionary<string, object?> { ["orderId"] = "ORD-1001", ["reason"] = "damaged", ["idempotencyKey"] = "prepare-IDENTITY-CANARY" };
        var prepared = await session.Call("prepare_return", prepareArgs);
        using var first = JsonDocument.Parse(PipeSession.Text(prepared));
        var intentId = first.RootElement.GetProperty("intent").GetProperty("id").GetString()!;
        await session.Call("prepare_return", prepareArgs);
        session.Database.Store.ApproveReturn(intentId);
        var submitArgs = new Dictionary<string, object?> { ["intentId"] = intentId, ["idempotencyKey"] = "submit-IDENTITY-CANARY" };
        var ticket = await session.Call("request_return", submitArgs);
        using var created = JsonDocument.Parse(PipeSession.Text(ticket));
        var ticketId = created.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(PipeSession.Text(ticket), PipeSession.Text(await session.Call("request_return", submitArgs)));
        var audit = session.Database.Store.ReadAudit();
        Assert.Equal(4, audit.Count);
        foreach (var row in audit)
        {
            Assert.Equal("success", row.Outcome);
            Assert.Equal("ok", row.ResultCode);
            Assert.NotNull(row.DurationMs);
            using var metadata = JsonDocument.Parse(row.ResultMetadata!);
            Assert.Equal(row.Tool == "prepare_return" ? intentId : ticketId, metadata.RootElement.GetProperty("id").GetString());
        }
        var serialized = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain("IDENTITY-CANARY", serialized);
        Assert.DoesNotContain("Avery", serialized);
        Assert.DoesNotContain("totalCents", serialized);
    }
}

public sealed class IndependentAuditStartTests
{
    private sealed class UnavailableAudit : IAuditSink
    {
        public string BeginAudit(string actor, string tool, string safeArguments, string? correlationId = null) => throw new IOException("PRIVATE-START-CANARY");
        public void FinishAudit(string id, string outcome, string resultCode, long durationMs, string? resultMetadata = null) => throw new InvalidOperationException("must never complete an unstarted audit");
    }

    [Theory]
    [InlineData("get_order")]
    [InlineData("prepare_return")]
    [InlineData("request_return")]
    public async Task StartFailureNeverReachesAnyReadOrWriteHandler(string tool)
    {
        var executions = 0;
        await using var session = await PipeSession.Start(writes: true, auditFactory: _ => new UnavailableAudit(), configure: mcp =>
            mcp.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellation) =>
            {
                Interlocked.Increment(ref executions);
                return await next(context, cancellation);
            })));
        var intent = session.Database.Store.PrepareReturn("test:local", "ORD-1001", "damaged", "prepare-start-fixture");
        session.Database.Store.ApproveReturn(intent.Id);
        var arguments = tool switch
        {
            "get_order" => new Dictionary<string, object?> { ["orderId"] = "ORD-1001" },
            "prepare_return" => new Dictionary<string, object?> { ["orderId"] = "ORD-1003", ["reason"] = "damaged", ["idempotencyKey"] = "start-failure-key" },
            _ => new Dictionary<string, object?> { ["intentId"] = intent.Id, ["idempotencyKey"] = "submit-start-fail" }
        };
        var result = await session.Call(tool, arguments);
        using var error = JsonDocument.Parse(PipeSession.Text(result));
        Assert.True(result.IsError);
        Assert.Equal("audit_unavailable", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, executions);
        Assert.Empty(session.Database.Store.ReadAudit());
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + session.Database.Path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM return_intents),(SELECT count(*) FROM returns),(SELECT count(*) FROM return_intents WHERE consumed_at IS NOT NULL)";
        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(1L, rows.GetInt64(0));
        Assert.Equal(0L, rows.GetInt64(1));
        Assert.Equal(0L, rows.GetInt64(2));
    }
}
