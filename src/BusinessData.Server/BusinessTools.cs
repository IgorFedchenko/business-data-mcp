using System.ComponentModel;
using BusinessData.Core;
using ModelContextProtocol.Server;

namespace BusinessData.Server;

[McpServerToolType]
public sealed class BusinessTools(BusinessStore store)
{
    [McpServerTool(Name = "search_orders", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Search synthetic orders by order ID or customer name. Returns at most 20 orders; no arbitrary SQL.")]
    public IReadOnlyList<Order> SearchOrders([Description("Order ID or synthetic customer name, 1–80 characters.")] string query,
        [Description("Result limit from 1 to 20.")] int limit = 10) => store.SearchOrders(query, limit);

    [McpServerTool(Name = "get_order", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get a synthetic order, its customer, amount and fulfillment status by ID.")]
    public Order GetOrder(string orderId) => store.GetOrder(orderId);

    [McpServerTool(Name = "get_order_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get the fulfillment status of one synthetic order.")]
    public object GetOrderStatus(string orderId)
    {
        var order = store.GetOrder(orderId);
        return new { order.Id, order.Status };
    }

    [McpServerTool(Name = "get_customer_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get a synthetic customer and their latest 20 orders and latest 20 return tickets.")]
    public CustomerHistory GetCustomerHistory(string customerId) => store.GetCustomerHistory(customerId);

    [McpServerTool(Name = "prepare_return", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Create a return intent for a delivered synthetic order. Requires returns.write. Human approval via the local operator CLI is required within 10 minutes. This tool cannot approve anything. Reuse the same idempotency key only for identical arguments.")]
    public object PrepareReturn(string orderId,
        [Description("One of: damaged, wrong_item, no_longer_needed.")] string reason,
        [Description("Unique retry key, 8–100 characters; reuse on network retries.")] string idempotencyKey,
        RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> context, ToolSecurity security)
    {
        var intent = store.PrepareReturn(security.Resolve(context.User).Subject, orderId, reason, idempotencyKey);
        return new { intent, nextStep = "Ask a human operator to run ./scripts/run.sh approve-return " + intent.Id + ". Then call request_return. Never approve on the operator's behalf." };
    }

    [McpServerTool(Name = "request_return", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Submit a synthetic return ticket for an unexpired intent explicitly approved by a human operator. Requires returns.write and the same principal that prepared it. Replays safely with the original idempotency key. No money is refunded.")]
    public ReturnTicket RequestReturn(string intentId,
        [Description("Unique retry key, 8–100 characters. Keep it unchanged when retrying.")] string idempotencyKey,
        RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> context, ToolSecurity security) =>
        store.RequestReturn(security.Resolve(context.User).Subject, intentId, idempotencyKey);
}
