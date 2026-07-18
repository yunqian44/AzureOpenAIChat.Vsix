namespace AzureOpenAI.Vsix;

internal sealed class AzureOpenAIConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2025-01-01-preview";
    public string Deployment { get; set; } = string.Empty;
    public string WireApi { get; set; } = "chat_completions";
    public string? SystemPrompt { get; set; }
    public double Temperature { get; set; } = 0.2;
    public int? MaxTokens { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
}
