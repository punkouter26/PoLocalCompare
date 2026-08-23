namespace PoLocalCompare.Api.Features.Judging;

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
    /// this window wins the race and the judge stands down.
    /// </summary>
    /// <remarks>
    /// There is no floor — the value is used as configured, and the endpoint clamps only the
    /// per-duel override to 0–3600. (A previous version of this comment claimed a 30-second
    /// floor was "applied at validation time"; no such validation ever existed, and the shipped
    /// value is now 10.)
    ///
    /// At 10 seconds the judge decides nearly every duel, which is deliberate: a duel resolves
    /// while the user is still watching it rather than parking on the page waiting for a click.
    /// The human path is the Arena's vote buttons during the countdown. Widen this if you want
    /// verdicts to be genuinely human-first — see PRD §9 for why this reversed twice.
    /// </remarks>
    public int DelaySeconds { get; set; } = 10;

    /// <summary>
    /// Foundry deployment used as judge. Must be a deployment that exists in the configured
    /// Foundry resource; it does not have to be one of the seeded duelling models.
    /// </summary>
    /// <remarks>
    /// <c>gpt-5-nano</c> rather than <c>gpt-5.4-nano</c>: the judge does a constrained
    /// comparison of two documents and returns structured JSON against a fixed schema, which
    /// is not a task the newer nano does better. It is roughly four times cheaper per input
    /// token and three times cheaper per output token, and with <see cref="DelaySeconds"/> at
    /// 10 it decides very nearly every duel — so this is the most-executed model call in the
    /// app, not an occasional one.
    /// </remarks>
    public string Deployment { get; set; } = "gpt-5-nano";

    /// <summary>Ceiling on the judge call itself, so a hung judge cannot pin the duel queue.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Characters of each model's HTML given to the judge, per side.
    /// </summary>
    /// <remarks>
    /// Full documents blow the context budget, and what distinguishes prompt adherence is
    /// visible near the head and tail — which is why <c>FoundryDuelJudge.Truncate</c> keeps
    /// both ends and elides the middle rather than simply cutting off.
    ///
    /// Halved from 6,000 on 2026-08-22. Two sides at 6,000 characters was roughly 3,000 input
    /// tokens on every judged duel, and at a 10-second grace window that is nearly every duel.
    /// The head-and-tail shape means the discriminating parts of both documents still reach
    /// the judge at 3,000. If judgements look worse after this, raise it back — the value is
    /// configuration precisely because it is a quality/cost dial rather than a constant.
    /// </remarks>
    public int MaxOutputChars { get; set; } = 3000;

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
