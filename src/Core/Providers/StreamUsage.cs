namespace Core.Providers;

/// <summary>Token usage finalized once a streamed chat completion has fully completed.</summary>
public sealed record StreamUsage(int PromptTokens, int CompletionTokens);
