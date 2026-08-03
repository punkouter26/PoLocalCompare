using PoLocalCompare.Api.Common.Inference;

namespace PoLocalCompare.Unit;

public class FoundryChatRequestTests
{
    [Fact]
    public void Build_StreamingRequest_IncludesUsageMetadata()
    {
        var body = FoundryChatRequest.Build("phi-4", Array.Empty<object>(), 4096, 0.7, stream: true, includeModelField: false);

        Assert.True(body.ContainsKey("stream_options"));
    }

    [Fact]
    public void Build_ReasoningRequest_UsesCompletionBudgetWithoutTemperature()
    {
        var body = FoundryChatRequest.Build("gpt-5-nano", Array.Empty<object>(), 16_384, 0.7, stream: true, includeModelField: false);

        Assert.Equal(16_384, body["max_completion_tokens"]);
        Assert.False(body.ContainsKey("temperature"));
    }
}