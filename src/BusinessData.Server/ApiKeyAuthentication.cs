using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BusinessData.Server;

public sealed class ApiCredentials
{
    private readonly byte[] reader;
    private readonly byte[]? writer;

    public ApiCredentials(string? readKey, string? writeKey, bool allowWrites)
    {
        ValidateKey(readKey);
        reader = Hash(readKey!);
        if (!string.IsNullOrEmpty(writeKey))
        {
            ValidateKey(writeKey);
            var hash = Hash(writeKey);
            if (CryptographicOperations.FixedTimeEquals(reader, hash))
                throw new InvalidOperationException("Read and write API keys must be different.");
            if (allowWrites) writer = hash;
        }
        if (allowWrites && writer == null)
            throw new InvalidOperationException("MCP_WRITE_API_KEY is required when HTTP writes are enabled.");
    }

    public Caller? Authenticate(string key)
    {
        if (key.Length > 512) return null;
        var hash = Hash(key);
        if (CryptographicOperations.FixedTimeEquals(hash, reader)) return new("api:reader", true, false);
        if (writer != null && CryptographicOperations.FixedTimeEquals(hash, writer)) return new("api:writer", true, true);
        return null;
    }

    private static void ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 32 or > 512 || key.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("HTTP API keys must be 32–512 non-whitespace characters. Set MCP_READ_API_KEY and optionally MCP_WRITE_API_KEY.");
    }
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ApiCredentials credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var values) || values.Count != 1)
            return Task.FromResult(AuthenticateResult.NoResult());
        var actor = credentials.Authenticate(values[0] ?? "");
        if (actor == null) return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, actor.Subject), new("permission", "orders.read")];
        if (actor.CanWrite) claims.Add(new("permission", "returns.write"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "ApiKey";
        return Task.CompletedTask;
    }
}
