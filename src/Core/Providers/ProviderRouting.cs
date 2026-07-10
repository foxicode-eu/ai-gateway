namespace Core.Providers;

/// <summary>
/// Picks which provider a chat completion request should go to, based on the requested model name. This is a
/// stopgap: there's no explicit per-tenant provider configuration yet, so we infer from the model string
/// (matching each provider's real model naming). Revisit once tenants can configure routing explicitly —
/// tracked as an open item in ARCHITECTURE.md.
/// </summary>
public static class ProviderRouting
{
    public static string ResolveProviderName(string model) =>
        model.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ? "anthropic" : "openai";
}
