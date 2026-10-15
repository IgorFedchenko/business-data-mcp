using System.Text.Json;
using BusinessData.Core;
using BusinessData.Server;

try
{
    if (args.FirstOrDefault() is "approve-return" or "audit")
    {
        var store = new BusinessStore(ServerApplication.DatabasePath);
        store.Initialize();
        if (args[0] == "audit")
        {
            Console.WriteLine(JsonSerializer.Serialize(store.ReadAudit(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length != 2 || Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Usage: ./scripts/run.sh approve-return RI-... in an interactive terminal. No piped or automatic approval.");
            return 2;
        }
        var intent = store.GetIntent(args[1]);
        var order = store.GetOrder(intent.OrderId);
        Console.WriteLine($"Approve synthetic return for {order.Id}: {order.TotalCents / 100m:0.00} {order.Currency}");
        Console.WriteLine($"Reason: {intent.Reason}; principal: {intent.Actor}; expires: {intent.ExpiresAt}");
        Console.WriteLine("This creates a return ticket; no payment or refund is issued.");
        Console.Write($"Type the full intent ID ({intent.Id}) to approve: ");
        if (Console.ReadLine() != intent.Id) { Console.Error.WriteLine("Approval cancelled."); return 2; }
        var audit = store.BeginAudit("operator:local", "approve_return", JsonSerializer.Serialize(new { intent.Id, intent.OrderId }));
        try
        {
            store.ApproveReturn(intent.Id);
            store.FinishAudit(audit, "success", "approved", 0);
        }
        catch (BusinessException e)
        {
            store.FinishAudit(audit, "error", e.Code, 0);
            throw;
        }
        Console.WriteLine("Approved. The original principal may now call request_return before expiry.");
        return 0;
    }
    var config = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
    var transport = config["transport"] ?? "stdio";
    if (transport == "stdio")
    {
        using var host = ServerApplication.BuildStdio(args);
        await host.RunAsync();
    }
    else if (transport == "http")
    {
        await using var app = ServerApplication.Build(args);
        await app.RunAsync();
    }
    else throw new InvalidOperationException("Transport must be stdio or http.");
    return 0;
}
catch (BusinessException e)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { code = e.Code, message = e.Message }));
    return 1;
}
catch (InvalidOperationException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { code = "configuration_error", message = "Check transport, limits, distinct HTTP keys and database configuration." }));
    return 1;
}
catch (Exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { code = "startup_error", message = "Check local configuration and database access." }));
    return 1;
}
