using PoLocalCompare.Api.Common.Inference;

namespace PoLocalCompare.Unit;

public class FoundryChatRequestTests
{
    [Fact]
    public void Build_StreamingRequest_IncludesUsageMetadata()
    {
        // phi-4 is in the SupportsStreamUsage allow-list; native Azure OpenAI accepts the
        // OpenAI streaming-extension shape (stream_options.include_usage).
        var body = FoundryChatRequest.Build("phi-4", Array.Empty<object>(), 4096, 0.7, stream: true, includeModelField: false);

        Assert.True(body.ContainsKey("stream_options"));
    }

    [Fact]
    public void Build_StreamingRequestForCodestral_OmitsStreamOptions()
    {
        // Codestral 2501 routes through a strict OpenAI-compatible proxy that rejects
        // stream_options with HTTP 422 — see SCRIPTS/review/out/demo-run for the live 422.
        // The deny-by-default SupportsStreamUsage list keeps Codestral working without a flag
        // trip every time it gets paired in a demo.
        var body = FoundryChatRequest.Build("Codestral-2501", Array.Empty<object>(), 4096, 0.7, stream: true, includeModelField: false);

        Assert.False(body.ContainsKey("stream_options"));
        Assert.True(body.ContainsKey("stream"));
    }

    [Fact]
    public void Build_NonStreamingRequestForCodestral_StillWorks()
    {
        // stream=false: stream_options is moot. The body should still be well-formed.
        var body = FoundryChatRequest.Build("Codestral-2501", Array.Empty<object>(), 4096, 0.7, stream: false, includeModelField: false);

        Assert.False(body.ContainsKey("stream_options"));
        Assert.Equal(false, body["stream"]);
        Assert.Equal(4096, body["max_tokens"]);
    }

    [Theory]
    [InlineData("gpt-5-nano")]
    [InlineData("o1-preview")]
    [InlineData("phi-4-mini-instruct")]
    [InlineData("Llama-3.3-70B-Instruct")]
    [InlineData("grok-4-1-fast-non-reasoning")]
    public void Build_NativeDeploymentNames_SupportStreamUsage(string deployment)
    {
        // Pin the allow-list: these all went through PoLocalCompare's proxy during the
        // 2026-08-13 demo session and accepted stream_options without complaint.
        Assert.True(FoundryChatRequest.SupportsStreamUsage(deployment));
    }

    [Theory]
    [InlineData("Codestral-2501")]
    [InlineData("mistral-large-latest")]
    [InlineData("unknown-deployment")]
    [InlineData("")]
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
}