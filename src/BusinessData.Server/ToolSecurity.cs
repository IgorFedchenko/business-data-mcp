using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using BusinessData.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BusinessData.Server;

public sealed record Caller(string Subject, bool CanRead, bool CanWrite);

public sealed class CallQuota(int permitLimit, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (long Window, int Count)> windows = [];
    private readonly object gate = new();

    public bool Acquire(string subject)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
            windows.TryGetValue(subject, out var entry);
            var count = entry.Window == now ? entry.Count : 0;
            if (count >= permitLimit) return false;
            windows[subject] = (now, count + 1);
            return true;
        }
    }
}

public sealed class ToolSecurity(BusinessStore store, Caller localCaller, CallQuota quota, IAuditSink? auditSink = null, ILogger<ToolSecurity>? logger = null)
{
    public static readonly IReadOnlySet<string> ReadTools = new HashSet<string>
        { "search_orders", "get_order", "get_order_status", "get_customer_history" };
    public static readonly IReadOnlySet<string> WriteTools = new HashSet<string>
        { "prepare_return", "request_return" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Caller Resolve(ClaimsPrincipal? user) => user?.Identity?.IsAuthenticated == true
        ? new(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
            user.HasClaim("permission", "orders.read"), user.HasClaim("permission", "returns.write"))
        : localCaller;

    public bool CanDiscover(string name, ClaimsPrincipal? user)
    {
        var actor = Resolve(user);
        return ReadTools.Contains(name) ? actor.CanRead : WriteTools.Contains(name) && actor.CanWrite;
    }

    public async ValueTask<CallToolResult> Invoke(RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next, CancellationToken cancellationToken)
    {
        var actor = Resolve(context.User);
        var name = context.Params.Name;
        var knownTool = ReadTools.Contains(name) || WriteTools.Contains(name);
        var correlationId = Guid.NewGuid().ToString("N");
        var audit = auditSink ?? store;
        var safeName = knownTool ? name : "unknown_tool";
        using var activity = Telemetry.Activities.StartActivity("tools.call", ActivityKind.Server);
        activity?.SetTag("tool.name", safeName);
        activity?.SetTag("correlation.id", correlationId);
        var watch = Stopwatch.StartNew();
        string outcome = "error", resultCode = "internal_error";
        string? metadata = null;
        CallToolResult response;
        try
        {
            audit.BeginAudit(actor.Subject, safeName, Sanitize(context.Params.Arguments), correlationId);
        }
        catch (Exception)
        {
            Telemetry.Record(safeName, "audit_unavailable", watch.ElapsedMilliseconds, activity);
            logger?.LogError("Tool audit start failed. CorrelationId={CorrelationId} Tool={Tool} Code={Code}", correlationId, safeName, "audit_unavailable");
            return Error("audit_unavailable", "Audit storage unavailable; operation was not executed.", correlationId);
        }
        try
        {
            if (!quota.Acquire(actor.Subject))
                throw new BusinessException("rate_limited", "Tool quota exceeded. Retry after the next UTC minute.");
            if (!knownTool) throw new BusinessException("unknown_tool", "Unknown tool.");
            if (!CanDiscover(name, context.User))
                throw new BusinessException("forbidden", "This principal does not have the required permission.");
            ValidateArguments(name, context.Params.Arguments);
            cancellationToken.ThrowIfCancellationRequested();
            response = await next(context, cancellationToken);
            outcome = response.IsError == true ? "error" : "success";
            resultCode = response.IsError == true ? "internal_error" : "ok";
            if (response.IsError == true)
            {
                var safeMessage = "The operation could not be completed. Retry writes with the same idempotency key.";
                var errorText = response.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                try
                {
                    using var errorJson = JsonDocument.Parse(errorText ?? "{}");
                    var code = errorJson.RootElement.GetProperty("code").GetString();
                    if (code is "invalid_argument" or "not_found" or "ineligible_order" or "already_returned" or
                        "forbidden" or "expired_confirmation" or "confirmation_required" or "already_consumed" or "idempotency_conflict")
                    {
                        resultCode = code;
                        safeMessage = errorJson.RootElement.GetProperty("message").GetString()!;
                    }
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
                outcome = resultCode == "forbidden" ? "denied" : "error";
                response = Error(resultCode, safeMessage, correlationId);
            }
            if (outcome == "success") metadata = SafeResultMetadata(response);
        }
        catch (BusinessException e)
        {
            resultCode = e.Code;
            outcome = e.Code == "forbidden" ? "denied" : "error";
            response = Error(e.Code, e.Message, correlationId);
        }
        catch (OperationCanceledException)
        {
            resultCode = "cancelled";
            response = Error(resultCode, "Call cancelled. Retry writes with the same idempotency key.", correlationId);
        }
        catch (McpException e)
        {
            resultCode = "invalid_argument";
            var message = "Arguments do not match the tool schema.";
            try
            {
                using var document = JsonDocument.Parse(e.Message);
                var code = document.RootElement.GetProperty("code").GetString();
                if (code is "invalid_argument" or "not_found" or "ineligible_order" or "already_returned" or
                    "forbidden" or "expired_confirmation" or "confirmation_required" or "already_consumed" or "idempotency_conflict")
                {
                    resultCode = code;
                    message = document.RootElement.GetProperty("message").GetString()!;
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
            outcome = resultCode == "forbidden" ? "denied" : "error";
            response = Error(resultCode, message, correlationId);
        }
        catch (Exception e) when (e is ArgumentException or JsonException)
        {
            resultCode = "invalid_argument";
            response = Error(resultCode, "Arguments do not match the tool schema.", correlationId);
        }
        catch (Exception)
        {
            response = Error(resultCode, "The operation could not be completed. Retry writes with the same idempotency key.", correlationId);
        }
        // Completion is deliberately outside finally: a storage fault cannot replace a safe response.
        var originalCode = resultCode;
        try { audit.FinishAudit(correlationId, outcome, resultCode, watch.ElapsedMilliseconds, metadata); }
        catch (Exception)
        {
            var uncertain = WriteTools.Contains(name) && (outcome == "success" || resultCode is "internal_error" or "cancelled");
            resultCode = uncertain ? "outcome_unknown" : "audit_unavailable";
            response = Error(resultCode, uncertain
                ? "The write may have committed. Reuse the original idempotency key to recover its result."
                : "Audit completion failed; see originalCode for the operation outcome.", correlationId, originalCode);
        }
        Telemetry.Record(safeName, resultCode, watch.ElapsedMilliseconds, activity);
        logger?.LogInformation("Tool completed. CorrelationId={CorrelationId} Tool={Tool} Code={Code} OriginalCode={OriginalCode} DurationMs={DurationMs}",
            correlationId, safeName, resultCode, originalCode, watch.ElapsedMilliseconds);
        return response;
    }

    private static CallToolResult Error(string code, string message, string correlationId, string? originalCode = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { code, message, correlationId, originalCode }, Json) }]
    };

    private static string? SafeResultMetadata(CallToolResult result)
    {
        // SDK typed tool responses are JSON text. Retain only validated synthetic identities/counts.
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        if (text == null) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var safe = new Dictionary<string, object>();
            if (root.ValueKind == JsonValueKind.Array) safe["count"] = root.GetArrayLength();
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("intent", out var intent)) root = intent;
                foreach (var field in new[] { "id", "orderId", "intentId" })
                    if (root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
                        Regex.IsMatch(value.GetString()!, "^(ORD-[0-9]{4}|RI-[a-f0-9]{32}|RET-[a-f0-9]{32})$", RegexOptions.CultureInvariant))
                        safe[field] = value.GetString()!;
                if (root.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array)
                    safe["orderCount"] = orders.GetArrayLength();
            }
            return JsonSerializer.Serialize(safe, Json);
        }
        catch (JsonException) { return null; }
    }

    private static void ValidateArguments(string name, IDictionary<string, JsonElement>? args)
    {
        string[] required = name switch
        {
            "search_orders" => ["query"],
            "get_order" or "get_order_status" => ["orderId"],
            "get_customer_history" => ["customerId"],
            "prepare_return" => ["orderId", "reason", "idempotencyKey"],
            "request_return" => ["intentId", "idempotencyKey"],
            _ => []
        };
        if (args == null || required.Any(k => !args.TryGetValue(k, out var value) || value.ValueKind != JsonValueKind.String))
            throw new BusinessException("invalid_argument", "Required string arguments are missing or invalid.");
        if (args.Keys.Any(k => !required.Contains(k) && !(name == "search_orders" && k == "limit")))
            throw new BusinessException("invalid_argument", "Unexpected argument.");
        if (args.Values.Any(v => v.GetRawText().Length > 1024))
            throw new BusinessException("invalid_argument", "Argument exceeds the size limit.");
        if (args.TryGetValue("limit", out var limit) && (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var n) || n is < 1 or > 20))
            throw new BusinessException("invalid_argument", "Limit must be an integer from 1 to 20.");
    }

    public static string Sanitize(IDictionary<string, JsonElement>? arguments)
    {
        var safe = new Dictionary<string, object>();
        if (arguments == null) return "{}";
        foreach (var name in new[] { "orderId", "customerId", "intentId" })
        {
            if (!arguments.TryGetValue(name, out var value)) continue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var pattern = name switch { "orderId" => "^ORD-[0-9]{4}$", "customerId" => "^CUST-[0-9]{3}$", _ => "^RI-[a-f0-9]{32}$" };
            safe[name] = text != null && Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant) ? text : "[redacted]";
        }
        if (arguments.TryGetValue("reason", out var reason))
        {
            var value = reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
            safe["reason"] = value is "damaged" or "wrong_item" or "no_longer_needed" ? value : "[redacted]";
        }
        if (arguments.TryGetValue("limit", out var limit) && limit.ValueKind == JsonValueKind.Number && limit.TryGetInt32(out var n) && n is >= 1 and <= 20)
            safe["limit"] = n;
        if (arguments.ContainsKey("query")) safe["query"] = "[redacted]";
        if (arguments.ContainsKey("idempotencyKey")) safe["idempotencyKey"] = "[redacted]";
        safe["argumentCount"] = arguments.Count;
        return JsonSerializer.Serialize(safe, Json);
    }
}
