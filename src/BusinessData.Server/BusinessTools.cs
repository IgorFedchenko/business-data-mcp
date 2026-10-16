using System.ComponentModel;
using System.Text.Json;
using BusinessData.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BusinessData.Server;

[McpServerToolType]
public sealed class BusinessTools(BusinessStore store)
{
    [McpServerTool(Name = "search_orders", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search synthetic orders by order ID or customer name. Returns at most 20 orders; no arbitrary SQL.")]
    public IReadOnlyList<Order> SearchOrders([Description("Order ID or synthetic customer name, 1–80 characters.")] string query,
        [Description("Result limit from 1 to 20.")] int limit = 10) => Execute(() => store.SearchOrders(query, limit));

    [McpServerTool(Name = "get_order", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a synthetic order, its customer, amount and fulfillment status by ID.")]
    public Order GetOrder([Description("Synthetic order ID, for example ORD-1001.")] string orderId) => Execute(() => store.GetOrder(orderId));

    [McpServerTool(Name = "get_order_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get the fulfillment status of one synthetic order.")]
    public OrderStatus GetOrderStatus([Description("Synthetic order ID, for example ORD-1001.")] string orderId)
    {
        var order = Execute(() => store.GetOrder(orderId));
        return new(order.Id, order.Status);
    }

    [McpServerTool(Name = "get_customer_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a synthetic customer and their latest 20 orders and latest 20 return tickets.")]
    public CustomerHistory GetCustomerHistory([Description("Synthetic customer ID, for example CUST-001.")] string customerId) => Execute(() => store.GetCustomerHistory(customerId));

    [McpServerTool(Name = "prepare_return", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create a return intent for a delivered synthetic order. Requires returns.write. Human approval via the local operator CLI is required within 10 minutes. This tool cannot approve anything. Reuse the same idempotency key only for identical arguments.")]
    public PreparedReturn PrepareReturn([Description("Delivered synthetic order ID, for example ORD-1001.")] string orderId,
        [Description("One of: damaged, wrong_item, no_longer_needed.")] string reason,
        [Description("Unique retry key, 8–100 characters; reuse on network retries.")] string idempotencyKey,
        RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> context, ToolSecurity security)
    {
        var intent = Execute(() => store.PrepareReturn(security.Resolve(context.User).Subject, orderId, reason, idempotencyKey));
        return new(intent, "Ask a human operator to run ./scripts/run.sh approve-return " + intent.Id + ". Then call request_return. Never approve on the operator's behalf.");
    }

    [McpServerTool(Name = "request_return", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Submit a synthetic return ticket for an unexpired intent explicitly approved by a human operator. Requires returns.write and the same principal that prepared it. Replays safely with the original idempotency key. No money is refunded.")]
    public ReturnTicket RequestReturn([Description("Full RI- ID returned by prepare_return for this same principal.")] string intentId,
        [Description("Unique retry key, 8–100 characters. Keep it unchanged when retrying.")] string idempotencyKey,
        RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> context, ToolSecurity security) =>
        Execute(() => store.RequestReturn(security.Resolve(context.User).Subject, intentId, idempotencyKey));
    // The SDK masks ordinary exceptions before the outer filter sees them. Only our
    // authored domain errors are safe to expose; unknown exceptions retain SDK masking.
    private static T Execute<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (BusinessException error)
        {
            throw new McpException(JsonSerializer.Serialize(new { code = error.Code, message = error.Message }));
        }
    }
}
