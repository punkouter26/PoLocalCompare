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
    /// Raised from <c>gpt-5-nano</c> on 2026-08-23. The cheap-nano argument was that judging is
    /// a constrained comparison returning JSON against a fixed schema, and that the judge is the
    /// most-executed model call in the app. Both are still true — but a duel asking for a
    /// rotating cube was won by a document that rendered a flat plane, and reading two HTML
    /// documents closely enough to tell those apart is not a task the cheapest model in the
    /// catalog does adequately. The judge decides where every ELO point goes; it is the one
    /// call in the app where being wrong is permanent.
    /// </remarks>
    public string Deployment { get; set; } = "gpt-5.4-mini";

    /// <summary>Ceiling on the judge call itself, so a hung judge cannot pin the duel queue.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Show the judge a screenshot of each rendered page as well as its source.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately: it needs headless Chromium on the host, which the
    /// Free-tier App Service does not have and has no room for. Turn it on where a browser
    /// exists — locally it is already installed for the E2E-UI suite. When it is on but a
    /// screenshot cannot be produced, the judge silently reads source only; a duel is never
    /// left unjudged because a render failed.
    ///
    /// The deployment named by <see cref="Deployment"/> must accept image input. If it does not,
    /// the call fails and <see cref="AutoJudge"/> leaves the duel Pending rather than guessing —
    /// so change both together, or leave this off.
    /// </remarks>
    public bool VisionEnabled { get; set; }

    /// <summary>
    /// Characters of each model's HTML given to the judge, per side.
    /// </summary>
    /// <remarks>
    /// Full documents blow the context budget, and what distinguishes prompt adherence is
    /// visible near the head and tail — which is why <c>FoundryDuelJudge.Truncate</c> keeps
    /// both ends and elides the middle rather than simply cutting off.
    ///
    /// Was 6,000, halved to 3,000 on 2026-08-22 to cut tokens, and raised to 12,000 on
    /// 2026-08-23 because that saving had a cost nobody had measured: at 3,000 the elided
    /// middle is where the substance of a generated page lives, and a cube-versus-plane duel
    /// was decided by a judge that had probably never seen the geometry code. Judging is the
    /// one call where cheapness buys a wrong permanent answer, so this dial is set for accuracy
    /// now. Lower it if judged-duel cost becomes the binding constraint.
    /// </remarks>
    public int MaxOutputChars { get; set; } = 12_000;

    /// <summary>
    /// Maximum number of times a single duel may be re-queued after a transient HTTP 429.
    /// The first attempt counts; e.g. 3 means up to two retries on top of the original call.
    /// Foundry rate-limit windows typically reset per-minute, so two retries with a one-minute
    /// gap is enough to ride out a short burst; anything more is a sustained quota problem
    /// the operator should know about, not silence.
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
