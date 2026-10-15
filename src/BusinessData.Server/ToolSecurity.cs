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

public sealed class ToolSecurity(BusinessStore store, Caller localCaller, CallQuota quota)
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
        // Never put arbitrary tool names or argument values supplied by a caller into the audit log.
        var auditId = store.BeginAudit(actor.Subject, knownTool ? name : "unknown_tool", Sanitize(context.Params.Arguments));
        var watch = Stopwatch.StartNew();
        string outcome = "error", resultCode = "internal_error";
        try
        {
            if (!quota.Acquire(actor.Subject))
                throw new BusinessException("rate_limited", "Tool quota exceeded. Retry after the next UTC minute.");
            if (!knownTool) throw new BusinessException("unknown_tool", "Unknown tool.");
            if (!CanDiscover(name, context.User))
                throw new BusinessException("forbidden", "This principal does not have the required permission.");
            ValidateArguments(name, context.Params.Arguments);
            var result = await next(context, cancellationToken);
            outcome = result.IsError == true ? "error" : "success";
            resultCode = result.IsError == true ? "tool_error" : "ok";
            return result;
        }
        catch (BusinessException e)
        {
            resultCode = e.Code;
            outcome = e.Code == "forbidden" ? "denied" : "error";
            return Error(e.Code, e.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resultCode = "cancelled";
            return Error("cancelled", "Call cancelled. Retry writes with the same idempotency key.");
        }
        catch (Exception e) when (e is ArgumentException or JsonException or McpException)
        {
            resultCode = "invalid_argument";
            return Error("invalid_argument", "Arguments do not match the tool schema.");
        }
        catch (Exception)
        {
            return Error("internal_error", "The operation could not be completed. Retry writes with the same idempotency key.");
        }
        finally
        {
            // A started record is durable before execution. Audit storage failure never silently succeeds.
            store.FinishAudit(auditId, outcome, resultCode, watch.ElapsedMilliseconds);
        }
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { code, message }, Json) }]
    };

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
