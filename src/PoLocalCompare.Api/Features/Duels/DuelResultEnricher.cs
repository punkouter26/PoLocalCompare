// SOLID: Single Responsibility — one home for post-inference result metrics
using System.Text;
using System.Text.RegularExpressions;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// Centralizes post-inference enrichment of a <see cref="DuelResult"/>: character density,
/// HTML output-quality score, and (for energy-rated models) GreenStats. Pure domain logic with no I/O —
/// both the server-side execution path and the client-result endpoint call this so a result is scored
/// identically regardless of where inference ran.
/// </summary>
/// <remarks>GoF: Strategy is avoided deliberately; enrichment is uniform across model types, so a single
/// static policy keeps the call sites trivial and the math discoverable in one place.</remarks>
public static partial class DuelResultEnricher
{
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Mutates <paramref name="result"/> in place; safe to call more than once (idempotent).</summary>
    public static void Enrich(DuelResult result, Model model, double electricityRateUsdPerKwh)
    {
        var html = result.HtmlOutputRaw ?? string.Empty;

        result.CharacterDensityRatio = ComputeCharacterDensity(html);
        result.OutputQualityScore = HtmlOutputQualityScorer.Score(html);

        var isEnergyRated = model.ModelType is ModelType.Local or ModelType.LocalService;
        if (isEnergyRated && model.TdpWatts.HasValue && !result.IsFailure)
        {
            var energyWh = GreenStatsCalculator.ComputeEnergyWh(model.TdpWatts.Value, result.TotalDurationMs);
            result.EnergyWh = energyWh;
            result.EnergyCostUsd = GreenStatsCalculator.ComputeEnergyCostUsd(energyWh, electricityRateUsdPerKwh);
            result.GreenScore = GreenStatsCalculator.ComputeGreenScore(result.TokenCount, energyWh);
        }
    }

    /// <summary>Ratio of functional (non-whitespace, comment-free) characters to total UTF-8 bytes.</summary>
    private static double ComputeCharacterDensity(string html)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(html);
        if (totalBytes == 0)
            return 0;

        var noComments = HtmlCommentRegex().Replace(html, string.Empty);
        var collapsed = WhitespaceRegex().Replace(noComments, " ").Trim();
        var functionalChars = collapsed.Replace(" ", string.Empty).Length;
        return Math.Round(new CharacterDensity(functionalChars, totalBytes).Value, 4);
    }
}
