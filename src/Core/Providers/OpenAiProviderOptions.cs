using System.ComponentModel.DataAnnotations;

namespace Core.Providers;

public sealed class OpenAiProviderOptions
{
    public const string ConfigurationSection = "Providers:OpenAI";

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.openai.com/";
}
