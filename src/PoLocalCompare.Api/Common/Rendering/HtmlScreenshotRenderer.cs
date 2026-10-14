using Microsoft.Playwright;
using PoLocalCompare.Api.Common.Inference;

namespace PoLocalCompare.Api.Common.Rendering;

public sealed record HtmlRenderEvidence(
    IReadOnlyList<byte[]> Frames,
    string InteractionSummary,
    byte[]? InteractionScreenshot);

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
    public async Task<byte[]?> RenderAsync(string? html, CancellationToken cancellationToken) =>
        (await RenderCoreAsync(html, motion: false, cancellationToken))?.Frames[0];

    /// <summary>
    /// Like <see cref="RenderAsync"/>, but a page that looks animated is shot twice,
    /// <see cref="FrameGap"/> apart. One frame cannot show motion, and most curated prompts ask
    /// for it ("rotating continuously") — two identical frames are how the judge can tell a
    /// cube that spins from one that was merely drawn. A static page still returns one frame.
    /// </summary>
    public Task<HtmlRenderEvidence?> RenderFramesAsync(string? html, CancellationToken cancellationToken) =>
        RenderCoreAsync(html, motion: true, cancellationToken);

    /// <summary>Gap between the two frames of an animated page.</summary>
    public static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(800);

    private async Task<HtmlRenderEvidence?> RenderCoreAsync(string? html, bool motion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var browser = await GetBrowserAsync(cancellationToken);
        if (browser is null) return null;

        IBrowserContext? context = null;
        try
        {
            // One context per render, closed in the finally. This was a shared context cached
            // in a field "for both sides of a duel" — but the finally below closed it after
            // every render without clearing the field, so every later render got a dead context,
            // and the two sides render concurrently, so the first to finish closed it under the
            // second. Every screenshot failed with TargetClosedException and every duel was
            // judged from source alone, silently, because failure degrades by design. ~200 ms
            // per render is the price of a context nothing else can close.
            context = await NewContextAsync(browser);
            await context.RouteAsync("**/*", route => route.AbortAsync());

            var page = await context.NewPageAsync();
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 5_000,
            });

            // Static HTML is fully painted the moment the DOM loads; only wait for animations.
            var interactive = LooksInteractive(html);
            IReadOnlyList<byte[]> frames;
            if (motion && interactive)
            {
                // Same total budget as one long settle, split around the two shots.
                await page.WaitForTimeoutAsync((float)(SettleDelay - FrameGap).TotalMilliseconds);
                var first = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
                await page.WaitForTimeoutAsync((float)FrameGap.TotalMilliseconds);
                var second = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
                frames = [first, second];
            }
            else
            {
                var settle = interactive ? SettleDelay : StaticSettleDelay;
                await page.WaitForTimeoutAsync((float)settle.TotalMilliseconds);
                frames = [await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png })];
            }

            var interaction = await ProbeInteractionsAsync(page, frames[^1]);
            return new HtmlRenderEvidence(frames, interaction.Summary, interaction.Screenshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
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

    private static async Task<(string Summary, byte[]? Screenshot)> ProbeInteractionsAsync(
        IPage page,
        byte[] initialScreenshot)
    {
        var controls = page.Locator(
            "button:visible:not([disabled]), input[type=button]:visible:not([disabled]), input[type=submit]:visible:not([disabled])");
        var count = Math.Min(await controls.CountAsync(), 3);
        if (count == 0)
            return ("No visible enabled button was available for the runtime interaction probe.", null);

        var observations = new List<string>(count);
        var previousScreenshot = initialScreenshot;
        byte[]? changedScreenshot = null;

        for (var i = 0; i < count; i++)
        {
            var control = controls.Nth(i);
            var label = await control.GetAttributeAsync("aria-label");
            if (string.IsNullOrWhiteSpace(label))
                label = await control.InnerTextAsync();
            label = ClipProbeText(label);

            var beforeText = ClipProbeText(await page.Locator("body").InnerTextAsync());
            try
            {
                await control.ClickAsync(new LocatorClickOptions { Timeout = 1_500 });
                await page.WaitForTimeoutAsync(150);
                var afterText = ClipProbeText(await page.Locator("body").InnerTextAsync());
                var afterScreenshot = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
                var textChanged = !string.Equals(beforeText, afterText, StringComparison.Ordinal);
                var pixelsChanged = !previousScreenshot.AsSpan().SequenceEqual(afterScreenshot);

                observations.Add(
                    $"Activated button \"{label}\": visible text {(textChanged ? "changed" : "did not change")}" +
                    $"{(textChanged ? $" from \"{beforeText}\" to \"{afterText}\"" : string.Empty)}; " +
                    $"rendered pixels {(pixelsChanged ? "changed" : "did not change")}.");

                if (textChanged || pixelsChanged)
                    changedScreenshot = afterScreenshot;
                previousScreenshot = afterScreenshot;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                observations.Add($"Could not activate button \"{label}\" during the bounded runtime probe.");
            }
        }

        return (string.Join(" ", observations), changedScreenshot);
    }

    private static string ClipProbeText(string? value)
    {
        var normalized = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 140 ? normalized : normalized[..137] + "...";
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

    private static Task<IBrowserContext> NewContextAsync(IBrowser browser) =>
        browser.NewContextAsync(new BrowserNewContextOptions
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
