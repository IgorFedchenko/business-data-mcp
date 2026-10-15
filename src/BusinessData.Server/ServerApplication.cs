using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using BusinessData.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;

namespace BusinessData.Server;

public static class ServerApplication
{
    public static string DatabasePath => Environment.GetEnvironmentVariable("MCP_DATABASE") ?? ".data/business.db";

    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
        if (builder.Configuration["MCP_TELEMETRY_CONSOLE"] == "true") Telemetry.EnableSafeConsole();
        var store = new BusinessStore(builder.Configuration["MCP_DATABASE"] ?? DatabasePath);
        store.Initialize();
        builder.Services.AddBusinessMcp(store, new("stdio:local", true, builder.Configuration["MCP_ALLOW_WRITES"] == "true"),
            ReadLimit(builder.Configuration, "MCP_TOOL_LIMIT", 30)).WithStdioServerTransport();
        return builder.Build();
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        // SDK request/argument tracing must never be enabled by a caller's logging preferences.
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        var writes = builder.Configuration["MCP_ALLOW_WRITES"] == "true";
        var toolLimit = ReadLimit(builder.Configuration, "MCP_TOOL_LIMIT", 30);
        if (builder.Configuration["MCP_TELEMETRY_CONSOLE"] == "true") Telemetry.EnableSafeConsole();
        var store = new BusinessStore(builder.Configuration["MCP_DATABASE"] ?? DatabasePath);
        store.Initialize();
        var mcp = builder.Services.AddBusinessMcp(store, new("anonymous", false, false), toolLimit);

        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5050");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Configuration["AllowedHosts"] ??= "localhost;127.0.0.1;[::1]";
        builder.Services.AddSingleton(new ApiCredentials(builder.Configuration["MCP_READ_API_KEY"],
            builder.Configuration["MCP_WRITE_API_KEY"], writes));
        builder.Services.AddAuthentication("ApiKey").AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });
        builder.Services.AddAuthorization();
        var requestLimit = ReadLimit(builder.Configuration, "MCP_HTTP_LIMIT", 120);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("mcp", http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = requestLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers["Retry-After"] = "60";
                return ValueTask.CompletedTask;
            };
        });
        mcp.WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless);
        var app = builder.Build();
        var allowedOrigins = (builder.Configuration["MCP_ALLOWED_ORIGINS"] ?? "http://localhost:5050,http://127.0.0.1:5050")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                if (!context.Request.IsHttps && context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip))
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                else if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                         (origin.Count != 1 || !allowedOrigins.Contains(origin[0] ?? "")))
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                else if (context.Request.ContentLength > 64 * 1024)
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                else await next(context);
                if (context.Response.StatusCode >= 400)
                {
                    try
                    {
                        var id = store.BeginAudit(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous", "http.access", "{}");
                        store.FinishAudit(id, "denied", "http_" + context.Response.StatusCode, 0);
                    }
                    catch (Exception)
                    {
                        // The rejection remains a rejection even if its audit cannot be persisted.
                        app.Logger.LogError("HTTP rejection audit unavailable. Code={Code}", "audit_unavailable");
                    }
                }
            }
            else await next(context);
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapMcp("/mcp").RequireAuthorization().RequireRateLimiting("mcp");
        app.MapGet("/health", () => Results.Ok(new { status = "ok", data = "synthetic" }));
        return app;
    }

    private static int ReadLimit(IConfiguration config, string name, int fallback)
    {
        if (config[name] == null) return fallback;
        if (int.TryParse(config[name], out var limit) && limit is >= 1 and <= 1000) return limit;
        throw new InvalidOperationException(name + " must be an integer from 1 to 1000.");
    }
}
