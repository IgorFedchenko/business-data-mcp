using System.Text.Json;
using BusinessData.Server;

namespace BusinessData.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void ApiKeysHaveSeparatePermissionsAndFailClosed()
    {
        var credentials = new ApiCredentials(new('r', 40), new('w', 40), true);
        Assert.False(credentials.Authenticate(new('r', 40))!.CanWrite);
        Assert.True(credentials.Authenticate(new('w', 40))!.CanWrite);
        Assert.Null(credentials.Authenticate("invalid"));
        Assert.Null(new ApiCredentials(new('r', 40), new('w', 40), false).Authenticate(new('w', 40)));
        Assert.Throws<InvalidOperationException>(() => new ApiCredentials("short", null, false));
        Assert.Throws<InvalidOperationException>(() => new ApiCredentials(new('r', 40), new('r', 40), true));
        Assert.Throws<InvalidOperationException>(() => new ApiCredentials(new('r', 40), null, true));
    }

    [Fact]
    public void AuditRedactsQueriesSecretsUnknownArgumentsAndInvalidIdentifiers()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement("secret-query"),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement("secret-key"),
            ["apiKey"] = JsonSerializer.SerializeToElement("secret-api"),
            ["orderId"] = JsonSerializer.SerializeToElement("ORD-secret-value"),
            ["reason"] = JsonSerializer.SerializeToElement("secret-reason"),
            ["limit"] = JsonSerializer.SerializeToElement(999)
        };
        var audit = ToolSecurity.Sanitize(args);
        Assert.DoesNotContain("secret", audit);
        Assert.DoesNotContain("apiKey", audit);
        Assert.Contains("redacted", audit);
        Assert.Contains("argumentCount", audit);
    }

    [Fact]
    public void ToolQuotaIsPerPrincipalAndResetsOnTheNextMinute()
    {
        var clock = new TestClock();
        var quota = new CallQuota(1, clock);
        Assert.True(quota.Acquire("reader"));
        Assert.False(quota.Acquire("reader"));
        Assert.True(quota.Acquire("writer"));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(quota.Acquire("reader"));
    }
}
