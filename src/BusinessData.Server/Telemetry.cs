using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace BusinessData.Server;

/// <summary>OpenTelemetry-compatible instrumentation. No exporter or outbound network by default.</summary>
public static class Telemetry
{
    public const string SourceName = "BusinessData.Mcp";
    public static readonly ActivitySource Activities = new(SourceName, "1.0.0");
    public static readonly Meter Metrics = new(SourceName, "1.0.0");
    private static readonly Counter<long> Calls = Metrics.CreateCounter<long>("mcp.tool.calls");
    private static readonly Histogram<double> Duration = Metrics.CreateHistogram<double>("mcp.tool.duration", "ms");

    public static void Record(string tool, string code, long duration, Activity? activity)
    {
        var tags = new TagList { { "tool.name", tool }, { "outcome.code", code } };
        Calls.Add(1, tags);
        Duration.Record(duration, tags);
        activity?.SetTag("outcome.code", code);
        activity?.SetStatus(code == "ok" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    public static void EnableSafeConsole()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                type = "span",
                traceId = activity.TraceId.ToString(),
                correlationId = activity.GetTagItem("correlation.id"),
                tool = activity.GetTagItem("tool.name"),
                code = activity.GetTagItem("outcome.code"),
                durationMs = activity.Duration.TotalMilliseconds
            }))
        });
    }
}
