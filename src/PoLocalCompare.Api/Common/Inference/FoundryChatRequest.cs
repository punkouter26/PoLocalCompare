namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Builds Azure AI Foundry / Azure OpenAI chat-completion request bodies that are
/// compatible with both classic chat models and GPT-5 / o-series reasoning models.
///
/// Reasoning models (gpt-5*, o1/o3/o4) reject <c>max_tokens</c> (they require
/// <c>max_completion_tokens</c>) and reject any non-default <c>temperature</c>,
/// returning HTTP 400. Classic models use <c>max_tokens</c> and honour temperature.
/// </summary>
public static class FoundryChatRequest
{
    /// <summary>API version that supports <c>max_completion_tokens</c> for reasoning models.</summary>
    public const string ApiVersion = "2024-12-01-preview";

    public static bool IsReasoningModel(string? deploymentName)
    {
        if (string.IsNullOrWhiteSpace(deploymentName)) return false;
        var n = deploymentName.ToLowerInvariant();
        return n.StartsWith("gpt-5")
            || n.StartsWith("o1")
            || n.StartsWith("o3")
            || n.StartsWith("o4");
    }

    /// <summary>
    /// Builds a request-body dictionary ready for <c>JsonSerializer.Serialize</c>.
    /// For reasoning models, <paramref name="temperature"/> is omitted and the token
    /// budget is sent as <c>max_completion_tokens</c>.
    /// </summary>
    /// <param name="includeModelField">
    /// True for the <c>/models/chat/completions</c> inference endpoint (which requires a
    /// top-level <c>model</c> field); false for the per-deployment endpoint.
    /// </param>
    public static Dictionary<string, object?> Build(
        string deploymentName,
        object messages,
        int maxTokens,
        double temperature,
        bool stream,
        bool includeModelField)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["stream"] = stream,
        };

        if (includeModelField)
        {
            body["model"] = deploymentName;
        }

        if (IsReasoningModel(deploymentName))
        {
            body["max_completion_tokens"] = maxTokens;
            // temperature intentionally omitted — reasoning models only allow the default.
        }
        else
        {
            body["max_tokens"] = maxTokens;
            body["temperature"] = temperature;
        }

        return body;
    }
}
