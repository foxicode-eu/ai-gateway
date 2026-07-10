namespace Core.Providers;

public sealed class OpenAiProviderOptions
{
    public const string ConfigurationSection = "Providers:OpenAI";

    public string BaseUrl { get; set; } = "https://api.openai.com/";
}
