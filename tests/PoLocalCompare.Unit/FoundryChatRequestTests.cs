using System.Text.Json;
using PoLocalCompare.Api.Common.Inference;

namespace PoLocalCompare.Unit;

public class FoundryChatRequestTests
{
    [Fact]
    public void Build_ForCodestral_OmitsStreamOptions_StreamingOrNot()
    {
        // Codestral 2501 routes through a strict OpenAI-compatible proxy that rejects
        // stream_options with HTTP 422 (observed live during the 2026-08-13 demo session).
        // The deny-by-default SupportsStreamUsage list keeps Codestral working without a flag
        // trip every time it gets paired in a demo. With stream=false the option is moot, but
        // the body still has to come out well-formed — both halves of that are asserted here.
        var streaming = FoundryChatRequest.Build("Codestral-2501", Array.Empty<object>(), 4096, 0.7, stream: true, includeModelField: false);

        Assert.False(streaming.ContainsKey("stream_options"));
        Assert.True(streaming.ContainsKey("stream"));

        var blocking = FoundryChatRequest.Build("Codestral-2501", Array.Empty<object>(), 4096, 0.7, stream: false, includeModelField: false);

        Assert.False(blocking.ContainsKey("stream_options"));
        Assert.Equal(false, blocking["stream"]);
        Assert.Equal(4096, blocking["max_tokens"]);
    }

    [Theory]
    [InlineData("gpt-5-nano")]
    [InlineData("Llama-3.3-70B-Instruct")]
    public void Build_NativeDeploymentNames_SupportStreamUsage(string deployment)
    {
        // Pin the allow-list: these all went through PoLocalCompare's proxy during the
        // 2026-08-13 demo session and accepted stream_options without complaint.
        Assert.True(FoundryChatRequest.SupportsStreamUsage(deployment));
    }

    [Theory]
    [InlineData("Codestral-2501")]
    [InlineData(null)]
    public void Build_NonNativeOrUnknownDeploymentNames_RejectStreamUsage(string? deployment)
    {
        // Deny-by-default: anything not on the native list must NOT get stream_options.
        // Add a deployment to SupportsStreamUsage only after confirming the upstream accepts
        // the OpenAI streaming-extension shape — see the ADR for Codestral.
        Assert.False(FoundryChatRequest.SupportsStreamUsage(deployment));
    }

    [Fact]
    public void Build_ReasoningRequest_UsesCompletionBudgetWithoutTemperature()
    {
        var body = FoundryChatRequest.Build("gpt-5-nano", Array.Empty<object>(), 16_384, 0.7, stream: true, includeModelField: false);

        Assert.Equal(16_384, body["max_completion_tokens"]);
        Assert.False(body.ContainsKey("temperature"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetCachedBody_ProducesTheSameShapeAsBuild(bool includeModelField)
    {
        // The template cache shipped with the sentinel pre-quoted, so the serialized body
        // carried an *escaped* token the substitution search never matched — every Foundry
        // duel died on "User-prompt sentinel missing from cached body template." before a
        // request went out. It also handed Build a single message object, which dropped the
        // system prompt and made "messages" an object rather than an array. Parsing the body
        // back catches both: a shape defect cannot hide behind a string compare.
        const string system = "You are a \"strict\" HTML generator.\nNo prose.";
        const string user = "Build a stopwatch — \"one screen\", 100ms ticks.\backslash";

        var json = FoundryChatRequest.GetCachedBody(
            "gpt-5-nano", system, user, 8_192, 0.2, stream: true, includeModelField: includeModelField);

        using var parsed = JsonDocument.Parse(json);
        var messages = parsed.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(system, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(user, messages[1].GetProperty("content").GetString());

        if (includeModelField)
        {
            Assert.Equal("gpt-5-nano", parsed.RootElement.GetProperty("model").GetString());
        }
        else
        {
            Assert.False(parsed.RootElement.TryGetProperty("model", out _));
        }
    }

    [Fact]
    public void GetCachedBody_ReusedTemplate_SwapsOnlyTheUserPrompt()
    {
        // Second call for the same deployment is served from the cached prefix/suffix — the
        // path that matters in production, where every duel after the first hits it.
        const string system = "system prompt";
        _ = FoundryChatRequest.GetCachedBody("phi-4", system, "first", 4_096, 0.2, stream: true, includeModelField: false);
        var second = FoundryChatRequest.GetCachedBody("phi-4", system, "second", 4_096, 0.2, stream: true, includeModelField: false);

        using var parsed = JsonDocument.Parse(second);
        Assert.Equal(system, parsed.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("second", parsed.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(4_096, parsed.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void GetCachedBody_DifferentSystemPrompts_DoNotShareATemplate()
    {
        // The cache key covers everything baked into the prefix; a caller that varies the
        // system prompt (or the budget) must not be served another caller's body.
        var a = FoundryChatRequest.GetCachedBody("phi-4", "prompt A", "u", 4_096, 0.2, stream: false, includeModelField: false);
        var b = FoundryChatRequest.GetCachedBody("phi-4", "prompt B", "u", 4_096, 0.2, stream: false, includeModelField: false);

        Assert.Equal("prompt A", JsonDocument.Parse(a).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("prompt B", JsonDocument.Parse(b).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }
}
