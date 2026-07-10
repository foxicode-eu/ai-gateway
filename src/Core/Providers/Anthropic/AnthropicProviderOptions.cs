namespace Core.Providers.Anthropic;

public sealed class AnthropicProviderOptions
{
    public const string ConfigurationSection = "Providers:Anthropic";

    public string BaseUrl { get; set; } = "https://api.anthropic.com/";

    public string ApiVersion { get; set; } = "2023-06-01";
}
