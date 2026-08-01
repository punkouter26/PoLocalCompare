using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// Shared Playwright harness. Drives a real browser against a running instance across the
/// mandated mobile and desktop viewports (standards §4).
///
/// Prerequisites — this suite is NOT run in CI (no GPU on the runner), so invoke it manually:
///   1. App running at BASE_URL (default https://localhost:5001).
///   2. Browsers installed: <c>pwsh bin/Debug/net10.0/playwright.ps1 install chromium</c>.
/// Authentication uses the dev guest bypass via <c>/e2e/seed-auth</c>, which sets the BFF cookie.
/// Set <c>HEADLESS=1</c> to run without a visible window.
/// </summary>
public abstract class UiTestBase : IAsyncLifetime
{
    /// <summary>Every UI journey runs on both viewports; mobile portrait is the primary target.</summary>
    public static TheoryData<int, int> Viewports => new()
    {
        { 390, 844 },    // mobile portrait
        { 1440, 900 },   // desktop
    };

    protected static string BaseUrl =>
        Environment.GetEnvironmentVariable("BASE_URL")?.TrimEnd('/') ?? "https://localhost:5001";

    private static bool Headless =>
        Environment.GetEnvironmentVariable("HEADLESS") is "1" or "true";

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = Headless });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _pw.Dispose();
    }

    /// <param name="colorScheme">Emulates the OS light/dark preference for theming tests.</param>
    protected async Task<IPage> NewPageAsync(int width, int height, ColorScheme? colorScheme = null)
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = width, Height = height },
            IsMobile = width < 768,
            HasTouch = width < 768,
            ColorScheme = colorScheme,
        });
        return await context.NewPageAsync();
    }

    /// <summary>
    /// Opens the collapsed nav drawer on narrow viewports. Below 768px the session controls and
    /// the theme toggle live inside <c>.navbar-collapse</c>, which is display:none until the
    /// hamburger is pressed — so a mobile test has to press it, exactly as a person would.
    /// A no-op on desktop, where the bar is always expanded.
    /// </summary>
    protected static async Task OpenNavIfCollapsedAsync(IPage page)
    {
        var toggler = page.Locator(".navbar-toggler");
        if (await toggler.IsVisibleAsync())
        {
            await toggler.ClickAsync();
            await page.Locator(".navbar-collapse.nav-open").WaitForAsync(new() { Timeout = 5_000 });
        }
    }

    /// <summary>Signs in as a guest and lands on <paramref name="path"/>.</summary>
    protected async Task<IPage> SignedInPageAsync(int width, int height, string path = "/", ColorScheme? colorScheme = null)
    {
        var page = await NewPageAsync(width, height, colorScheme);
        await page.GotoAsync($"/e2e/seed-auth?redirect={Uri.EscapeDataString(path)}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });
        return page;
    }
}
