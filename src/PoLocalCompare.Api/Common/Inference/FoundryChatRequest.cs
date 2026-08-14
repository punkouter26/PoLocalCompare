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

    /// <summary>
    /// Per-deployment chat route. A name that is not a deployment in this resource 404s here
    /// but may still resolve on <see cref="ModelInferenceUrl"/> — every Foundry caller runs
    /// that two-endpoint fallback, so both URLs are built here rather than at each call site.
    /// </summary>
    public static string DeploymentUrl(string endpoint, string deploymentName) =>
        $"{endpoint}/openai/deployments/{deploymentName}/chat/completions?api-version={ApiVersion}";

    /// <summary>
    /// Model-inference chat route — the 404 fallback for <see cref="DeploymentUrl"/>. Requires
    /// the model named in the body, so pair it with <c>Build(..., includeModelField: true)</c>.
    /// </summary>
    public static string ModelInferenceUrl(string endpoint) =>
        $"{endpoint}/models/chat/completions?api-version={ApiVersion}";

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
    /// Whether a streaming chat-completion request to this deployment may include
    /// <c>stream_options: { include_usage = true }</c>. True for native Azure OpenAI deployments
    /// (the GPT/Phi/Llama/Grok families we route today); false for deployments proxied through
    /// a stricter OpenAI-compatible endpoint (e.g. Codestral 2501 via the Mistral MaaS route)
    /// that reject the extra field with HTTP 422 — see AutoJudgeTests' codestral pin.
    /// </summary>
    /// <remarks>
    /// Deny-by-default: a deployment that does not match the known-native prefix list gets
    /// the lean body. Add a model to the list only after confirming the upstream accepts the
    /// OpenAI streaming-extension shape.
    /// </remarks>
    public static bool SupportsStreamUsage(string? deploymentName)
    {
        if (string.IsNullOrWhiteSpace(deploymentName)) return false;
        var n = deploymentName.ToLowerInvariant();
        // Native Azure OpenAI deployments — accept stream_options without complaint.
        return n.StartsWith("gpt-")
            || n.StartsWith("o1")
            || n.StartsWith("o3")
            || n.StartsWith("o4")
            || n.StartsWith("phi-")
            || n.StartsWith("llama-")
            || n.StartsWith("llama_")
            || n.StartsWith("grok-");
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

        if (stream && SupportsStreamUsage(deploymentName))
        {
            // Codestral 2501 (and any other strict OpenAI-compatible proxy) rejects this with
            // HTTP 422 — "Extra inputs are not permitted" on stream_options.include_usage. Only
            // native Azure OpenAI deployments accept the OpenAI streaming-extension shape; see
            // SupportsStreamUsage for the deny-by-default list.
            body["stream_options"] = new { include_usage = true };
        }

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
