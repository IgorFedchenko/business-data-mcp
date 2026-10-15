namespace BusinessData.Core;

public sealed record Customer(string Id, string Name, string Tier);
public sealed record Order(string Id, string CustomerId, string CustomerName, string Status,
    long TotalCents, string Currency, string PlacedAt);
public sealed record ReturnIntent(string Id, string Actor, string OrderId, string Reason,
    string ExpiresAt, string? ApprovedAt, string? ConsumedAt);
public sealed record ReturnTicket(string Id, string OrderId, string IntentId, string Status, string CreatedAt);
public sealed record CustomerHistory(Customer Customer, IReadOnlyList<Order> Orders, IReadOnlyList<ReturnTicket> Returns);
public sealed record AuditEntry(string Id, string Timestamp, string Actor, string Tool, string Arguments,
    string Outcome, string? ResultCode, long? DurationMs);

public sealed class BusinessException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
