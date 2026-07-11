namespace Core.Sessions;

/// <summary>Named <c>Gateway</c>SessionOptions, not <c>SessionOptions</c>, to avoid colliding with ASP.NET
/// Core's own <c>Microsoft.AspNetCore.Builder.SessionOptions</c> (an unrelated built-in feature we don't use).</summary>
public sealed class GatewaySessionOptions
{
    public const string ConfigurationSection = "Sessions";

    /// <summary>"Redis" (production) or "InMemory" (local dev/tests only).</summary>
    public string Store { get; set; } = "Redis";

    /// <summary>Required when <see cref="Store"/> is "Redis".</summary>
    public string? RedisConnectionString { get; set; }

    public int IdleTimeoutMinutes { get; set; } = 480;

    public string CookieName { get; set; } = "ai_gateway_session";

    /// <summary>False for local HTTP development; a real deployment behind HTTPS must set this true.</summary>
    public bool CookieSecure { get; set; } = true;
}
