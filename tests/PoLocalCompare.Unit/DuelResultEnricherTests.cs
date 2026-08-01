using PoLocalCompare.Api.Common.Domain;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

/// <summary>
/// Both inference paths funnel through <see cref="DuelResultEnricher"/> — the server-side proxy
/// and the browser's local-result POST — so a duel is only fairly scored if this produces the
/// same numbers for both. These tests are the guard on that symmetry.
/// </summary>
public class DuelResultEnricherTests
{
    private const double Rate = 0.12;

    private static Model LocalModel(double? tdp = 115) =>
        new(ModelId.From("local-1"), "Local", ModelType.Local, tdpWatts: tdp, webLlmModelId: "llm-1");

    private static Model OllamaModel(double? tdp = 115) =>
        new(ModelId.From("svc-1"), "Ollama", ModelType.LocalService, tdpWatts: tdp, apiEndpointRef: "llama3.2");

    private static Model RemoteModel() =>
        new(ModelId.From("remote-1"), "Remote", ModelType.Remote, apiEndpointRef: "gpt-5-nano");

    private static DuelResult Result(string html, int tokens = 500, long ms = 30_000) =>
        new(DuelId.From("d1"), ModelId.From("m1"))
        {
            HtmlOutputRaw = html,
            TokenCount = tokens,
            TotalDurationMs = ms,
        };

    // ── Normalization happens here, once, for every path ───────────────────

    [Fact]
    public void Enrich_StripsMarkdownFencesFromStoredOutput()
    {
        var result = Result("```html\n<html><body>Hi</body></html>\n```");

        DuelResultEnricher.Enrich(result, RemoteModel(), Rate);

        Assert.Equal("<html><body>Hi</body></html>", result.HtmlOutputRaw);
    }

    [Fact]
    public void Enrich_RecomputesSizeFromTheNormalizedHtml()
    {
        var result = Result("```html\n<p>x</p>\n```");

        DuelResultEnricher.Enrich(result, RemoteModel(), Rate);

        Assert.Equal(result.HtmlOutputRaw.Length, result.HtmlOutputSizeBytes);
    }

    [Fact]
    public void Enrich_IsIdempotent()
    {
        var result = Result("```html\n<html><body>Hi</body></html>\n```");
        var model = LocalModel();

        DuelResultEnricher.Enrich(result, model, Rate);
        var first = (result.HtmlOutputRaw, result.HtmlOutputSizeBytes, result.OutputQualityScore,
                     result.CharacterDensityRatio, result.EnergyWh, result.GreenScore);

        DuelResultEnricher.Enrich(result, model, Rate);
        var second = (result.HtmlOutputRaw, result.HtmlOutputSizeBytes, result.OutputQualityScore,
                      result.CharacterDensityRatio, result.EnergyWh, result.GreenScore);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Enrich_EmptyOutput_ScoresZeroWithoutThrowing()
    {
        var result = Result(string.Empty);

        DuelResultEnricher.Enrich(result, RemoteModel(), Rate);

        Assert.Equal(0, result.OutputQualityScore);
        Assert.Equal(0, result.CharacterDensityRatio);
        Assert.Equal(0, result.HtmlOutputSizeBytes);
    }

    // ── Character density ──────────────────────────────────────────────────

    [Fact]
    public void Enrich_DensityIgnoresWhitespaceAndComments()
    {
        var dense = Result("<div>abcdefghij</div>");
        var padded = Result("<div>abcdefghij</div>\n\n    <!-- a long explanatory comment -->    ");

        DuelResultEnricher.Enrich(dense, RemoteModel(), Rate);
        DuelResultEnricher.Enrich(padded, RemoteModel(), Rate);

        // Same functional characters, more total bytes, so the padded output must score lower.
        Assert.True(padded.CharacterDensityRatio < dense.CharacterDensityRatio);
    }

    [Fact]
    public void Enrich_DensityIsBetweenZeroAndOne()
    {
        var result = Result("<html>  <body>  <p>hello</p>  </body>  </html>");

        DuelResultEnricher.Enrich(result, RemoteModel(), Rate);

        Assert.InRange(result.CharacterDensityRatio, 0, 1);
    }

    // ── Green stats apply only to energy-rated models ──────────────────────

    [Fact]
    public void Enrich_LocalModel_GetsGreenStats()
    {
        var result = Result("<html><body>x</body></html>");

        DuelResultEnricher.Enrich(result, LocalModel(), Rate);

        Assert.NotNull(result.EnergyWh);
        Assert.NotNull(result.EnergyCostUsd);
        Assert.NotNull(result.GreenScore);
    }

    [Fact]
    public void Enrich_OllamaModel_GetsGreenStats()
    {
        var result = Result("<html><body>x</body></html>");

        DuelResultEnricher.Enrich(result, OllamaModel(), Rate);

        Assert.NotNull(result.EnergyWh);
        Assert.NotNull(result.GreenScore);
    }

    [Fact]
    public void Enrich_RemoteModel_GetsNoGreenStats()
    {
        // A cloud model's energy use is not ours to measure; reporting a number would
        // put an invented value on the Green Score leaderboard.
        var result = Result("<html><body>x</body></html>");

        DuelResultEnricher.Enrich(result, RemoteModel(), Rate);

        Assert.Null(result.EnergyWh);
        Assert.Null(result.EnergyCostUsd);
        Assert.Null(result.GreenScore);
    }

    [Fact]
    public void Enrich_EnergyRatedModelWithoutTdp_GetsNoGreenStats()
    {
        // LocalService, not Local: the Model constructor rejects a Local model with no TdpWatts,
        // so an Ollama model is the only energy-rated shape that can actually reach the
        // enricher without one. Without a TDP there is nothing to compute energy from.
        var result = Result("<html><body>x</body></html>");

        DuelResultEnricher.Enrich(result, OllamaModel(tdp: null), Rate);

        Assert.Null(result.EnergyWh);
        Assert.Null(result.GreenScore);
    }

    [Fact]
    public void Model_RejectsALocalModelWithNoTdp()
    {
        // Pins the invariant the test above depends on.
        Assert.Throws<ArgumentException>(() =>
            new Model(ModelId.From("local-1"), "Local", ModelType.Local, tdpWatts: null, webLlmModelId: "llm-1"));
    }

    [Fact]
    public void Enrich_FailedResult_GetsNoGreenStats()
    {
        // A failure burned energy but produced no tokens; scoring it would reward
        // failing fast with an infinite-looking efficiency.
        var result = Result(string.Empty);
        result.IsFailure = true;

        DuelResultEnricher.Enrich(result, LocalModel(), Rate);

        Assert.Null(result.EnergyWh);
        Assert.Null(result.GreenScore);
    }

    [Fact]
    public void Enrich_GreenStatsMatchTheCalculator()
    {
        var result = Result("<html><body>x</body></html>", tokens: 500, ms: 30_000);

        DuelResultEnricher.Enrich(result, LocalModel(tdp: 115), Rate);

        var expectedWh = GreenStatsCalculator.ComputeEnergyWh(115, 30_000);
        Assert.Equal(expectedWh, result.EnergyWh);
        Assert.Equal(GreenStatsCalculator.ComputeEnergyCostUsd(expectedWh, Rate), result.EnergyCostUsd);
        Assert.Equal(GreenStatsCalculator.ComputeGreenScore(500, expectedWh), result.GreenScore);
    }

    [Fact]
    public void Enrich_LongerRunForSameTokens_ScoresLessGreen()
    {
        var quick = Result("<html><body>x</body></html>", tokens: 500, ms: 10_000);
        var slow = Result("<html><body>x</body></html>", tokens: 500, ms: 60_000);

        DuelResultEnricher.Enrich(quick, LocalModel(), Rate);
        DuelResultEnricher.Enrich(slow, LocalModel(), Rate);

        Assert.True(slow.GreenScore < quick.GreenScore);
    }

    [Fact]
    public void Enrich_QualityScoreMatchesTheScorer()
    {
        const string html = "<!DOCTYPE html><html><body><script>x</script></body></html>";
        var result = Result(html);

        DuelResultEnricher.Enrich(result, RemoteModel(), Rate);

        Assert.Equal(HtmlOutputQualityScorer.Score(html), result.OutputQualityScore);
    }

    [Fact]
    public void Enrich_ScoresFencedAndBareOutputIdentically()
    {
        // The core fairness property: a model that wraps its answer in a markdown fence must
        // not be penalised against one that does not.
        const string html = "<!DOCTYPE html><html><body><script>x</script></body></html>";
        var bare = Result(html);
        var fenced = Result($"```html\n{html}\n```");

        DuelResultEnricher.Enrich(bare, LocalModel(), Rate);
        DuelResultEnricher.Enrich(fenced, LocalModel(), Rate);

        Assert.Equal(bare.OutputQualityScore, fenced.OutputQualityScore);
        Assert.Equal(bare.CharacterDensityRatio, fenced.CharacterDensityRatio);
        Assert.Equal(bare.HtmlOutputSizeBytes, fenced.HtmlOutputSizeBytes);
    }
}
