using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PoLocalCompare.Api.Common.Inference;

public static class InferenceTelemetry
{
    public const string Name = "PoLocalCompare.Inference";

    public static readonly ActivitySource ActivitySource = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gen_ai.client.operation.duration", "s");
    private static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("gen_ai.usage.output_tokens", "token");
    private static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("gen_ai.usage.input_tokens", "token");

    public static void Record(string provider, string model, DuelResult result)
    {
        var tags = new TagList
        {
            { "gen_ai.provider.name", provider },
            { "gen_ai.request.model", model },
            { "gen_ai.response.finish_reasons", result.FinishReason ?? "unknown" },
            { "gen_ai.response.truncated", result.WasTruncated },
            { "error.type", result.IsFailure ? result.FailureReason ?? "inference_failure" : string.Empty },
        };

        Duration.Record(result.TotalDurationMs / 1000d, tags);
        OutputTokens.Add(result.TokenCount, tags);
        if (result.PromptTokenCount is { } promptTokens)
            InputTokens.Add(promptTokens, tags);
    }
}