using System.IO.Pipelines;
using BusinessData.Server;
using BusinessData.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BusinessData.Tests;

public sealed class PipeSession : IAsyncDisposable
{
    public TestDatabase Database { get; } = new();
    private IHost host = null!;
    public McpClient Client { get; private set; } = null!;

    public static async Task<PipeSession> Start(bool writes = false, int limit = 30, Func<BusinessStore, IAuditSink>? auditFactory = null, Action<IMcpServerBuilder>? configure = null)
    {
        var session = new PipeSession();
        var input = new Pipe();
        var output = new Pipe();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var mcp = builder.Services.AddBusinessMcp(session.Database.Store, new("test:local", true, writes), limit)
            .WithStreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream());
        configure?.Invoke(mcp);
        if (auditFactory != null) builder.Services.AddSingleton(auditFactory(session.Database.Store));
        session.host = builder.Build();
        await session.host.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        session.Client = await McpClient.CreateAsync(new StreamClientTransport(input.Writer.AsStream(), output.Reader.AsStream()),
            cancellationToken: timeout.Token);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StopAsync(timeout.Token);
        host.Dispose();
        Database.Dispose();
    }

    public async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await Client.CallToolAsync(name, args, cancellationToken: timeout.Token);
    }
    public static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}

public sealed class InMemoryMcpTests
{
    [Fact]
    public async Task ReadOnlyDiscoveryAndExecutionUseTheSameAuthorizationBoundary()
    {
        await using var session = await PipeSession.Start();
        var tools = await session.Client.ListToolsAsync();
        Assert.Equal(4, tools.Count);
        Assert.DoesNotContain(tools, t => t.Name == "prepare_return");
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations!.ReadOnlyHint));
        var result = await session.Call("get_order", new() { ["orderId"] = "ORD-1001" });
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Avery Morgan", PipeSession.Text(result));
        var denied = await session.Call("prepare_return", new()
            { ["orderId"] = "ORD-1001", ["reason"] = "damaged", ["idempotencyKey"] = "prepare-0001" });
        Assert.True(denied.IsError);
        Assert.Contains("forbidden", PipeSession.Text(denied));
        Assert.Empty(session.Database.Store.GetCustomerHistory("CUST-001").Returns);
        var audit = session.Database.Store.ReadAudit();
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Outcome == "denied" && a.ResultCode == "forbidden");
        Assert.Contains(audit, a => a.Outcome == "success" && a.Actor == "test:local");
    }

    [Fact]
    public async Task WriterCannotSelfApproveButCanSubmitAndReplayAnOperatorApprovedIntent()
    {
        await using var session = await PipeSession.Start(writes: true);
        var tools = await session.Client.ListToolsAsync();
        Assert.Equal(6, tools.Count);
        Assert.DoesNotContain(tools, t => t.Name.Contains("approve", StringComparison.Ordinal));
        var prepareSchema = tools.Single(t => t.Name == "prepare_return").ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Equal(new[] { "idempotencyKey", "orderId", "reason" }, prepareSchema.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var prepared = await session.Call("prepare_return", new()
            { ["orderId"] = "ORD-1001", ["reason"] = "damaged", ["idempotencyKey"] = "prepare-0001" });
        Assert.NotEqual(true, prepared.IsError);
        var id = System.Text.RegularExpressions.Regex.Match(PipeSession.Text(prepared), "RI-[a-f0-9]{32}").Value;
        Assert.NotEmpty(id);
        var args = new Dictionary<string, object?> { ["intentId"] = id, ["idempotencyKey"] = "submit-0001" };
        var blocked = await session.Call("request_return", args);
        Assert.True(blocked.IsError);
        Assert.Contains("confirmation_required", PipeSession.Text(blocked));
        Assert.Empty(session.Database.Store.GetCustomerHistory("CUST-001").Returns);
        session.Database.Store.ApproveReturn(id); // Simulated human adapter; not available over MCP.
        var created = await session.Call("request_return", args);
        Assert.NotEqual(true, created.IsError);
        Assert.Contains("RET-", PipeSession.Text(created));
        Assert.Equal(PipeSession.Text(created), PipeSession.Text(await session.Call("request_return", args)));
        Assert.Single(session.Database.Store.GetCustomerHistory("CUST-001").Returns);
    }

    [Fact]
    public async Task InvalidUnknownAndRateLimitedCallsAreAuditedWithoutLeakingArguments()
    {
        await using var session = await PipeSession.Start(limit: 3);
        var invalid = await session.Call("get_order", new() { ["orderId"] = "ORD-1001", ["apiKey"] = "DO-NOT-LOG" });
        Assert.True(invalid.IsError);
        Assert.Contains("invalid_argument", PipeSession.Text(invalid));
        var unknown = await session.Call("secret-tool-name", new() { ["query"] = "DO-NOT-LOG" });
        Assert.True(unknown.IsError);
        await session.Call("get_order", new() { ["orderId"] = "ORD-9999" });
        var limited = await session.Call("get_order", new() { ["orderId"] = "ORD-1001" });
        Assert.Contains("rate_limited", PipeSession.Text(limited));
        var audit = session.Database.Store.ReadAudit();
        Assert.Equal(4, audit.Count);
        Assert.Contains(audit, a => a.ResultCode == "not_found");
        Assert.Contains(audit, a => a.ResultCode == "rate_limited");
        Assert.DoesNotContain("DO-NOT-LOG", System.Text.Json.JsonSerializer.Serialize(audit));
        Assert.DoesNotContain("secret-tool-name", System.Text.Json.JsonSerializer.Serialize(audit));
    }
}
