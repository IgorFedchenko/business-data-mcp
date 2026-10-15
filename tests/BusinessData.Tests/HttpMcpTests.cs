using System.Net;
using System.Net.Http.Json;
using BusinessData.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;

namespace BusinessData.Tests;

public sealed class HttpSession : IAsyncDisposable
{
    public static readonly string ReaderKey = new('r', 40);
    public static readonly string WriterKey = new('w', 40);
    public TestDatabase Database { get; } = new();
    public WebApplication App { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;

    public static async Task<HttpSession> Start(bool writes = true, int requestLimit = 120)
    {
        var session = new HttpSession();
        session.App = ServerApplication.Build(["--transport", "http"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MCP_DATABASE"] = session.Database.Path, ["MCP_READ_API_KEY"] = ReaderKey,
                ["MCP_WRITE_API_KEY"] = WriterKey, ["MCP_ALLOW_WRITES"] = writes ? "true" : "false",
                ["MCP_HTTP_LIMIT"] = requestLimit.ToString(), ["MCP_TOOL_LIMIT"] = "30"
            });
        });
        await session.App.StartAsync();
        session.Http = session.App.GetTestClient();
        return session;
    }

    public Task<McpClient> Connect(string key) => McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = new Uri("http://localhost/mcp"),
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = key },
        EnableStandaloneGetStream = false
    }, Http), cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Database.Dispose();
    }
}

public sealed class HttpMcpTests
{
    [Fact]
    public async Task HttpRequiresHeaderAuthenticationAndAuditsRejections()
    {
        await using var session = await HttpSession.Start();
        using var missing = await session.Http.PostAsJsonAsync("/mcp", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("ApiKey", missing.Headers.WwwAuthenticate.Single().Scheme);
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        invalid.Headers.Add("X-Api-Key", "DO-NOT-LOG");
        invalid.Content = JsonContent.Create(new { });
        using var denied = await session.Http.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var queryKey = await session.Http.PostAsJsonAsync("/mcp?api_key=" + HttpSession.ReaderKey, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, queryKey.StatusCode);
        var audit = session.Database.Store.ReadAudit();
        Assert.Equal(3, audit.Count);
        Assert.All(audit, a => Assert.Equal("http_401", a.ResultCode));
        Assert.DoesNotContain("DO-NOT-LOG", System.Text.Json.JsonSerializer.Serialize(audit));
    }

    [Fact]
    public async Task AuthenticatedHttpClientReadsAndWriteKeyIsSeparatelyScoped()
    {
        await using var session = await HttpSession.Start();
        await using var reader = await session.Connect(HttpSession.ReaderKey);
        Assert.Equal(4, (await reader.ListToolsAsync()).Count);
        var status = await reader.CallToolAsync("get_order_status", new Dictionary<string, object?> { ["orderId"] = "ORD-1002" });
        Assert.NotEqual(true, status.IsError);
        Assert.Contains("shipped", PipeSession.Text(status));
        var blocked = await reader.CallToolAsync("prepare_return", new Dictionary<string, object?>
            { ["orderId"] = "ORD-1001", ["reason"] = "damaged", ["idempotencyKey"] = "prepare-0001" });
        Assert.Contains("forbidden", PipeSession.Text(blocked));
        await using var writer = await session.Connect(HttpSession.WriterKey);
        Assert.Equal(6, (await writer.ListToolsAsync()).Count);
        var prepared = await writer.CallToolAsync("prepare_return", new Dictionary<string, object?>
            { ["orderId"] = "ORD-1001", ["reason"] = "damaged", ["idempotencyKey"] = "prepare-0001" });
        Assert.NotEqual(true, prepared.IsError);
        Assert.Contains("api:writer", PipeSession.Text(prepared));
        Assert.Contains(session.Database.Store.ReadAudit(), a => a.Actor == "api:reader" && a.Outcome == "success");
        Assert.Contains(session.Database.Store.ReadAudit(), a => a.Actor == "api:writer" && a.Outcome == "success");
    }

    [Fact]
    public async Task WriterKeyIsRejectedUnlessWritesAreExplicitlyEnabled()
    {
        await using var session = await HttpSession.Start(writes: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Add("X-Api-Key", HttpSession.WriterKey);
        using var response = await session.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UntrustedBrowserOriginAndOversizedBodyAreRejected()
    {
        await using var session = await HttpSession.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Add("X-Api-Key", HttpSession.ReaderKey);
        request.Headers.Add("Origin", "https://untrusted.invalid");
        using var response = await session.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        body.Headers.Add("X-Api-Key", HttpSession.ReaderKey);
        body.Content = new StringContent(new string('x', 65 * 1024));
        using var oversized = await session.Http.SendAsync(body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Empty(session.Database.Store.GetCustomerHistory("CUST-001").Returns);
    }

    [Fact]
    public async Task HttpQuotaReturns429AndRetryAfterWithoutRunningTools()
    {
        await using var session = await HttpSession.Start(requestLimit: 1);
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
            request.Headers.Add("X-Api-Key", HttpSession.ReaderKey);
            request.Content = JsonContent.Create(new { jsonrpc = "2.0", id = i + 1, method = "tools/list" });
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await session.Http.SendAsync(request);
            if (i == 1)
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
                Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter!.Delta);
            }
        }
        Assert.Contains(session.Database.Store.ReadAudit(), a => a.ResultCode == "http_429");
    }
}
