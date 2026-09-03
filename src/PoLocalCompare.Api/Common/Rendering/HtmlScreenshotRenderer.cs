using Microsoft.Playwright;
using PoLocalCompare.Api.Common.Inference;

namespace PoLocalCompare.Api.Common.Rendering;

/// <summary>
/// Renders a model's HTML output to a PNG, so the judge can look at the page instead of
/// reading its source.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a specific failure: a duel asking for a rotating cube was won by a
/// document that drew a flat plane. Nothing in the source says "this is a plane" — the shape is
/// the product of projection maths executed at run time — so a text-only judge has to simulate
/// the script in its head to tell the two apart, and does it badly. A screenshot makes the
/// difference obvious.
/// </para>
/// <para>
/// <b>Off unless configured.</b> Headless Chromium is a heavyweight dependency: a few hundred MB
/// of browser on disk and a process per render. The deploy target is an Azure App Service
/// <b>Free (F1)</b> plan, which has neither the disk nor the memory headroom for it, and no
/// browser is installed there — so <c>AiJudge:VisionEnabled</c> defaults to false and the judge
/// stays text-only in Production. Turn it on locally, where the browser is already present from
/// the E2E-UI suite, by setting the flag in <c>appsettings.Development.json</c>.
/// </para>
/// <para>
/// <b>Every failure degrades rather than throws.</b> A missing browser, a page that hangs, a
/// script that never settles — all of them return null, and <c>FoundryDuelJudge</c> falls back
/// to judging the source. A duel must never go unjudged because a screenshot did not render.
/// </para>
/// <para>
/// Registered as a singleton: launching Chromium takes on the order of a second, so the browser
/// is started once on first use and reused. Pages are per-render and always disposed.
/// </para>
/// </remarks>
public sealed class HtmlScreenshotRenderer : IAsyncDisposable
{
    /// <summary>
    /// Matches the Arena's preview frame (see <see cref="InferencePrompt.PreviewWidth"/>) so the
    /// judge sees the canvas the model was told to design for, at 2× for legible text.
    /// </summary>
    private const int DeviceScaleFactor = 2;

    /// <summary>
    /// How long to let a page settle before shooting it. Long enough for an animation to reach a
    /// representative frame, short enough that it cannot meaningfully delay a verdict.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// A static HTML page (no scripts/canvas/svg/animation) is fully painted the moment the DOM
    /// loads, so the settle wait would be pure latency. The detect is a heuristic; a CSS-only
    /// animation will still trigger the long wait, but every "render a card with a button"
    /// style duel — the majority of the catalog — settles in 100 ms instead of 1.2 s.
    /// </summary>
    private static readonly TimeSpan StaticSettleDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Tags whose presence indicates the rendered output is interactive and needs the full
    /// settle. A pure-CSS deck of cards that never animates does NOT need this — the test below
    /// also looks for <c>@keyframes</c>, <c>animation:</c> and <c>transition:</c> to catch that.
    /// </summary>
    private static readonly string[] InteractiveMarkers =
    [
        "<script",
        "<canvas",
        "<svg",
        "<iframe",
        "<video",
        "<audio",
    ];

    private readonly ILogger<HtmlScreenshotRenderer> _logger;
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _launchFailed;

    public HtmlScreenshotRenderer(ILogger<HtmlScreenshotRenderer> logger) => _logger = logger;

    /// <summary>
    /// Renders <paramref name="html"/> and returns the PNG bytes, or null if it could not be
    /// rendered for any reason.
    /// </summary>
    public async Task<byte[]?> RenderAsync(string? html, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var browser = await GetBrowserAsync(cancellationToken);
        if (browser is null) return null;

        IBrowserContext? context = null;
        try
        {
            // Reuse one context per duel across both sides. Launching a context costs ~200 ms
            // on Chromium even though it inherits from a singleton browser, so two renders for
            // one judge call otherwise pay 2 × launch + 2 × teardown. The context is read-only
            // (we never set cookies, never log in) so a single shared instance is safe.
            context = await GetOrCreateContextAsync(browser, cancellationToken);
            await context.RouteAsync("**/*", route => route.AbortAsync());

            var page = await context.NewPageAsync();
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 5_000,
            });

            // Static HTML is fully painted the moment the DOM loads; only wait for animations.
            var settle = LooksInteractive(html) ? SettleDelay : StaticSettleDelay;
            await page.WaitForTimeoutAsync((float)settle.TotalMilliseconds);

            return await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
        }
        catch (Exception ex)
        {
            // Includes the cancellation case: a judge that ran out of time judges the source.
            _logger.LogWarning(ex, "Judge screenshot failed; falling back to source-only judging.");
            return null;
        }
        finally
        {
            if (context is not null)
            {
                try { await context.CloseAsync(); } catch { /* the context is being torn down anyway */ }
            }
        }
    }

    /// <summary>
    /// Heuristic: does this document need the full settle wait? Returns true when an interactive
    /// tag is present OR a CSS animation/transition is declared. False otherwise (a static
    /// layout reaches its paint state the moment the DOM loads).
    /// </summary>
    private static bool LooksInteractive(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        for (var i = 0; i < InteractiveMarkers.Length; i++)
        {
            if (html.Contains(InteractiveMarkers[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return html.Contains("@keyframes", StringComparison.OrdinalIgnoreCase)
            || html.Contains("animation:", StringComparison.OrdinalIgnoreCase)
            || html.Contains("transition:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One context per judge call, shared across both sides. Re-created on demand so a render
    /// that throws still leaves the next judge call with a fresh, valid context.
    /// </summary>
    private IBrowserContext? _sharedContext;

    private async Task<IBrowserContext> GetOrCreateContextAsync(IBrowser browser, CancellationToken ct)
    {
        if (_sharedContext is not null) return _sharedContext;
        return _sharedContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = InferencePrompt.PreviewWidth,
                Height = InferencePrompt.PreviewHeight,
            },
            DeviceScaleFactor = DeviceScaleFactor,
            // The document is untrusted model output. Scripts are exactly what we need to run
            // to see the result — but no network, no storage and a throwaway context destroyed
            // straight after.
            JavaScriptEnabled = true,
        });
    }
    private async Task<IBrowser?> GetBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null) return _browser;
        if (_launchFailed) return null;

        await _launchGate.WaitAsync(cancellationToken);
        try
        {
            if (_browser is not null) return _browser;
            if (_launchFailed) return null;

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            _logger.LogInformation("Judge screenshot browser launched.");
            return _browser;
        }
        catch (Exception ex)
        {
            _launchFailed = true;
            _logger.LogWarning(ex,
                "Could not launch a browser for judge screenshots — the judge will read source only. " +
                "Install one with: pwsh tests/PoLocalCompare.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium");
            return null;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { /* shutting down */ }
        }
        _playwright?.Dispose();
        _launchGate.Dispose();
    }
}
