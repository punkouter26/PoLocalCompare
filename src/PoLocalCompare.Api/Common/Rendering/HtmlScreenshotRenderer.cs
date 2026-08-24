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
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width = InferencePrompt.PreviewWidth,
                    Height = InferencePrompt.PreviewHeight,
                },
                DeviceScaleFactor = DeviceScaleFactor,
                // The document is untrusted model output. It gets no JS-disabled treatment —
                // scripts are exactly what we need to run to see the result — but it also gets
                // no network, no storage and a throwaway context that is destroyed straight after.
                JavaScriptEnabled = true,
            });

            // Nothing a generated page asks for is worth fetching. Blocking the network keeps a
            // page that references a dead CDN from spending the whole settle window on timeouts,
            // and it means a screenshot cannot become an outbound request from the server.
            await context.RouteAsync("**/*", route => route.AbortAsync());

            var page = await context.NewPageAsync();
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 5_000,
            });

            await page.WaitForTimeoutAsync((float)SettleDelay.TotalMilliseconds);

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
    /// Launches Chromium on first use. A failed launch is remembered, so a host without a
    /// browser installed pays the failure once rather than on every judged duel.
    /// </summary>
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
