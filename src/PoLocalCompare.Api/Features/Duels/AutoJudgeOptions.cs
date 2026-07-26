namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// Configuration for the auto-judge (config section <c>AiJudge</c>).
/// </summary>
public sealed class AutoJudgeOptions
{
    public const string SectionName = "AiJudge";

    /// <summary>
    /// Master switch. Off means no code path can move ELO without a human, which is how the
    /// app behaved before the auto-judge existed.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Grace period between a duel finishing and the judge deciding. A human who picks inside
    /// this window wins the race and the judge stands down. Note that at very short values a
    /// person cannot realistically read both outputs, so effectively every duel becomes
    /// LLM-judged — widen this if you want the human path to be usable.
    /// </summary>
    public int DelaySeconds { get; set; } = 5;

    /// <summary>
    /// Foundry deployment used as judge. Must be a deployment that exists in the configured
    /// Foundry resource; it does not have to be one of the seeded duelling models.
    /// </summary>
    public string Deployment { get; set; } = "gpt-5.4-nano";

    /// <summary>Ceiling on the judge call itself, so a hung judge cannot pin the duel queue.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Characters of each model's HTML given to the judge. Full documents blow the context
    /// budget and the opening of a document is what distinguishes adherence in practice.
    /// </summary>
    public int MaxOutputChars { get; set; } = 6000;
}
