using System.Text.Json;

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
    /// Reasoning budget for GPT-5 / o-series deployments. See the note at the call site for why
    /// this workload wants the floor rather than the default.
    /// </summary>
    public const string ReasoningEffort = "minimal";

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

    /// <summary>
    /// Per-deployment, per-route JSON body template cache. The first call to
    /// <see cref="GetCachedBody"/> serializes the body once with stable placeholders for the
    /// user prompt and the model field; every later call returns the cached prefix/suffix and
    /// only the placeholder regions are replaced. Cuts the JsonSerializer + Dictionary
    /// allocation per duel side — the only thing that varies per call is the user prompt.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Prefix, string Suffix)> BodyTemplateCache = new(StringComparer.Ordinal);

    // Placeholders are bare tokens, and the form searched for in the serialized body is
    // produced by the serializer itself rather than written out by hand. Two things make a
    // hand-written literal wrong, and the original had both: a token that already carries its
    // own quotes comes out double-escaped, and STJ's default encoder escapes < and > to
    // < / >, so "<<<X>>>" never appears literally in the body. Either one makes the
    // substitution search miss, which is what threw "User-prompt sentinel missing from cached
    // body template." on every Foundry duel. Serializing the token here means the search
    // string is escaped exactly the way the body is, whatever the encoder does.
    private const string UserPromptToken = "<<<USER_PROMPT_SENTINEL>>>";
    private const string ModelFieldToken = "<<<MODEL_SENTINEL>>>";

    private static readonly string UserPromptTokenJson = JsonSerializer.Serialize(UserPromptToken);
    private static readonly string ModelFieldTokenJson = JsonSerializer.Serialize(ModelFieldToken);

    /// <summary>
    /// Returns the ready-to-send JSON body for a (deployment, route, stream) triple, with
    /// <paramref name="userPrompt"/> substituted into the user message slot.
    /// </summary>
    /// <remarks>
    /// The system prompt, max_tokens / max_completion_tokens, stream_options and reasoning
    /// effort are deployment-fixed and live in the cached prefix. Model and user-prompt bytes
    /// are substituted into the cached suffix region. Everything that shapes the template is
    /// in the cache key — route, stream shape, reasoning shape, token budget, temperature and
    /// the system prompt — so a caller that varies any of them cannot be served another
    /// caller's body.
    /// </remarks>
    public static string GetCachedBody(
        string deploymentName,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        bool stream,
        bool includeModelField)
    {
        var cacheKey = string.Join(
            '|',
            deploymentName,
            includeModelField ? "mi" : "dep",
            stream ? "s" : "n",
            maxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            temperature.ToString(System.Globalization.CultureInfo.InvariantCulture),
            systemPrompt.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            systemPrompt.GetHashCode(StringComparison.Ordinal).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var template = BodyTemplateCache.GetOrAdd(cacheKey, _ =>
        {
            // Same message shape as the uncached path: a real two-element array with the
            // system prompt first. Handing Build a single message object instead drops the
            // system prompt and sends "messages" as an object, which Foundry rejects.
            var messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = UserPromptToken },
            };
            var dictionary = Build(deploymentName, messages, maxTokens, temperature, stream, includeModelField: includeModelField);
            if (includeModelField)
            {
                // Force the model field to a token as well, so the cached suffix carries it as
                // a literal the per-call substitution replaces cleanly.
                dictionary["model"] = ModelFieldToken;
            }

            var serialized = JsonSerializer.Serialize(dictionary);
            var idx = serialized.IndexOf(UserPromptTokenJson, StringComparison.Ordinal);
            if (idx < 0) throw new InvalidOperationException("User-prompt sentinel missing from cached body template.");
            var prefix = serialized[..idx];
            var suffix = serialized[(idx + UserPromptTokenJson.Length)..];
            return (prefix, suffix);
        });

        var body = template.Prefix + JsonSerializer.Serialize(userPrompt) + template.Suffix;
        if (includeModelField)
        {
            body = body.Replace(ModelFieldTokenJson, JsonSerializer.Serialize(deploymentName), StringComparison.Ordinal);
        }
        return body;
    }

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

            // Reasoning effort was previously left at the service default, which meant every
            // GPT-5-family duel spent thousands of reasoning tokens before emitting a single
            // visible character — billed at output rates, and paid on the clock the TokenRace
            // is measuring. That is close to pure waste for this workload: the task is "return
            // an HTML document", the output format is fixed, and there is no multi-step problem
            // to think through. Minimal keeps the reasoning path available (these models reject
            // being asked to skip it) while spending as little of the budget on it as the API
            // allows.
            //
            // Deployments that predate the parameter ignore an unknown field rather than
            // rejecting it, so this is safe across the catalog.
            body["reasoning_effort"] = ReasoningEffort;
        }
        else
        {
            body["max_tokens"] = maxTokens;
            body["temperature"] = temperature;
        }

        return body;
    }
}
