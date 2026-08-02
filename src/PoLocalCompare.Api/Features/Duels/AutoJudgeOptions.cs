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
    ///
    /// A 30-second floor is applied at validation time: less than that and the countdown UI
    /// would race the reader rather than help them, and the Arena defaults to <c>0</c>
    /// (immediate) only for the unattended demo path.
    /// </summary>
    public int DelaySeconds { get; set; } = 30;

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

    /// <summary>
    /// Maximum number of times a single duel may be re-queued after a transient HTTP 429.
    /// The first attempt counts; e.g. 3 means up to two retries on top of the original call.
    /// Foundry rate-limit windows typically reset per-minute, so two retries with a one-minute
    /// gap is enough to ride out a short burst; anything more is a sustained quota problem
    /// the demo's user should know about, not silence.
    /// </summary>
    public int RateLimitRetryMax { get; set; } = 2;

    /// <summary>
    /// Hard ceiling on the per-attempt retry delay derived from the HTTP <c>Retry-After</c>
    /// header (or our own backoff if the header is absent). Caps the absolute delay so an
    /// adversarial header value — the hour-long reset, say — cannot park an unattended
    /// duel on the queue for hours.
    /// </summary>
    public int RateLimitRetryMaxDelaySeconds { get; set; } = 90;
}
