// TEST-ONLY process composition. Never referenced by production projects or published server.
using System.Text.Json;
using BusinessData.Core;
using BusinessData.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length != 4 || args[2] is not ("before_execution" or "after_business") ||
    args[3] is not ("prepare_return" or "request_return"))
    return 2;
var database = args[0];
var marker = args[1];
var phase = args[2];
var tool = args[3];
var store = new BusinessStore(database);
store.Initialize();
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
builder.Services.AddBusinessMcp(store, new("stdio:local", true, true), 1000).WithStdioServerTransport();
builder.Services.AddSingleton<IAuditSink>(new BarrierAudit(store, marker, phase, tool));
using var host = builder.Build();
await host.RunAsync();
return 0;

sealed class BarrierAudit(BusinessStore store, string marker, string phase, string tool) : IAuditSink
{
    private readonly Dictionary<string, string> tools = [];

    public string BeginAudit(string actor, string name, string arguments, string? correlationId = null)
    {
        var id = store.BeginAudit(actor, name, arguments, correlationId);
        lock (tools) tools[id] = name;
        if (name == tool && phase == "before_execution") StopAtBarrier(id);
        return id;
    }

    public void FinishAudit(string id, string outcome, string code, long durationMs, string? metadata = null)
    {
        string name;
        lock (tools) name = tools[id];
        if (name == tool && phase == "after_business" && outcome == "success" && code == "ok") StopAtBarrier(id);
        store.FinishAudit(id, outcome, code, durationMs, metadata);
    }

    private void StopAtBarrier(string correlation)
    {
        var temporary = marker + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { phase, tool, correlation }));
        File.Move(temporary, marker);
        // The external independent Python client must observe the durable state, then SIGKILL.
        Thread.Sleep(Timeout.Infinite);
        throw new InvalidOperationException("unreachable test barrier");
    }
}
