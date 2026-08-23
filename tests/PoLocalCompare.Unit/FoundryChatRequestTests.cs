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
}
