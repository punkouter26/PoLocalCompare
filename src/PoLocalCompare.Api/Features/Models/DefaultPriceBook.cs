using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Default list rates for the Azure AI Foundry catalog, used as the source of truth when
/// a seeded row's <c>InputTokenPricePerMillion</c> / <c>OutputTokenPricePerMillion</c> are
/// null because the deployment was added before its retail price was verified.
/// </summary>
/// <remarks>
/// Prices below are Microsoft Foundry list rates for the named deployment as of 2026-09-02.
/// Update in lockstep with the public price sheet
/// (https://azure.microsoft.com/en-us/pricing/details/ai-foundry/) — a stale number here is a
/// stale number on the ModelCard, the leaderboard's avg-$/duel column, the Arena total, and
/// the Cost challenge verdict. ChallengeAdjudicator treats unpriced models as zero spend, so
/// an unpriced flagship would otherwise win every MaxCost challenge outright; this resolver
/// is what stops that.
///
/// Match key is the deployment name as it appears on the wire (the model's
/// <c>ApiEndpointRef</c>), normalised to lowercase. The match is suffix-based on purpose:
/// "gpt-5.4-mini-2025-08-07" is a snapshot deployment of the same model and should inherit the
/// snapshot price.
/// </remarks>
public static class DefaultPriceBook
{
    /// <summary>Input USD per million tokens, output USD per million tokens.</summary>
    public record Rate(decimal InputPerMillion, decimal OutputPerMillion);

    private static readonly (string Suffix, Rate Rate)[] Table =
    [
        // OpenAI
        ("gpt-5.5",            new Rate(5.00m,  40.00m)),
        ("gpt-5.4",            new Rate(2.50m,  15.00m)),
        ("gpt-5.4-mini",       new Rate(0.75m,   4.50m)),
        ("gpt-5.4-nano",       new Rate(0.20m,   1.25m)),
        ("gpt-5-mini",         new Rate(0.40m,   2.40m)),
        ("gpt-5-nano",         new Rate(0.05m,   0.40m)),
        ("gpt-4.1",            new Rate(2.50m,  10.00m)),
        ("gpt-4.1-mini",       new Rate(0.40m,   1.60m)),
        ("gpt-4.1-nano",       new Rate(0.10m,   0.40m)),
        ("gpt-oss-120b",       new Rate(0.15m,   0.60m)),

        // Microsoft first-party
        ("phi-4",              new Rate(0.125m,  0.50m)),
        ("phi-4-mini",         new Rate(0.075m,  0.30m)),

        // Meta on Azure
        ("llama-3.3-70b",      new Rate(0.71m,   0.71m)),

        // Mistral
        ("codestral-2501",     new Rate(0.30m,   0.90m)),

        // xAI
        ("grok-4-1-fast",      new Rate(0.20m,   0.50m)),
        ("grok-4",             new Rate(3.00m,  15.00m)),
        ("grok-4-6",           new Rate(2.00m,  10.00m)),

        // Moonshot
        ("kimi-k2",            new Rate(0.60m,   2.50m)),

        // DeepSeek
        ("deepseek-v4",        new Rate(0.14m,   0.28m)),
    ];

    /// <summary>
    /// Returns the default list rate for a deployment name, or null if the deployment is
    /// not in the price book (in which case the seed comment warns the cost UI will be empty
    /// and cost challenges will treat the model as zero-spend — same behaviour as today).
    /// </summary>
    public static Rate? Resolve(string? deploymentName)
    {
        if (string.IsNullOrWhiteSpace(deploymentName)) return null;
        var n = deploymentName.ToLowerInvariant();
        foreach (var (suffix, rate) in Table)
        {
            if (n == suffix || n.StartsWith(suffix, StringComparison.Ordinal) || n.Contains("-" + suffix, StringComparison.Ordinal))
                return rate;
        }
        return null;
    }

    /// <summary>
    /// Backfills null prices on the supplied model list and returns the rows that actually
    /// changed. Logs each substitution through the supplied logger so an operator running with
    /// the seed can see exactly which entries were filled by the price book rather than the
    /// Azure retail API.
    /// </summary>
    public static IReadOnlyList<Model> Backfill(IEnumerable<Model> models, ILogger logger)
    {
        var updated = new List<Model>();
        foreach (var model in models)
        {
            if (model.InputTokenPricePerMillion.HasValue && model.OutputTokenPricePerMillion.HasValue)
                continue;

            var rate = Resolve(model.ApiEndpointRef);
            if (rate is null) continue;

            var patched = model.WithPricing(rate.InputPerMillion, rate.OutputPerMillion);
            updated.Add(patched);
            logger.LogInformation(
                "Backfilled pricing for {Model} ({Endpoint}): ${Input}/M in, ${Output}/M out (from default price book).",
                model.DisplayName, model.ApiEndpointRef,
                rate.InputPerMillion, rate.OutputPerMillion);
        }
        return updated;
    }
}
