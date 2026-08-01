using Microsoft.Playwright;

namespace PoLocalCompare.Tests.E2E.Ui;

/// <summary>
/// C# Playwright UI smoke tests. Drives a real browser against a running instance,
/// in headed Chrome across the mandated mobile and desktop viewports (standards §6).
///
/// Prerequisites (not run in CI per project policy — invoke manually):
///   1. App running at BASE_URL (default https://localhost:5001).
///   2. Browsers installed: `pwsh bin/Debug/net10.0/playwright.ps1 install chromium`.
/// Authentication uses the dev guest bypass via /e2e/seed-auth (sets the BFF session cookie).
/// Set <c>HEADLESS=1</c> to run without a visible browser window.
///
/// Tagged <c>Category=UI</c> so the headless API journeys in <c>Api/</c> can run alone:
/// <c>dotnet test tests/PoLocalCompare.Tests.E2E --filter Category!=UI</c>.
/// </summary>
[Trait("Category", "UI")]
public sealed class WizardUiTests : IAsyncLifetime
{
    /// <summary>Viewport matrix — every UI journey runs on both (standards §6: mobile + desktop).</summary>
    public static TheoryData<int, int> Viewports => new()
    {
        { 390, 844 },    // mobile portrait (mobile-first is the primary target)
        { 1440, 900 },   // desktop
    };

    private static string BaseUrl =>
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

    private async Task<IPage> NewPageAsync(int width, int height)
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = width, Height = height },
            IsMobile = width < 768,
            HasTouch = width < 768,
        });
        return await context.NewPageAsync();
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Login_ThenWizard_ShowsHeadingAndDisabledCompare(int width, int height)
    {
        var page = await NewPageAsync(width, height);

        // Guest sign-in (sets the BFF cookie). /war-room is a legacy stub that
        // redirects into the wizard at "/", so this also covers that hop.
        await page.GotoAsync("/e2e/seed-auth?redirect=/war-room");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        await Assertions.Expect(page.Locator(".wizard__title")).ToContainTextAsync("Compare two models");

        // Step 3's CTA stays disabled until two models and a prompt are chosen.
        var compare = page.Locator(".wizard__panel--cta .wizard__btn--primary");
        await Assertions.Expect(compare).ToBeVisibleAsync();
        await Assertions.Expect(compare).ToBeDisabledAsync();
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Unauthenticated_Root_ShowsLogin(int width, int height)
    {
        var page = await NewPageAsync(width, height);
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        // The auth gate renders the Login view (Sign in with Microsoft) before any page content.
        await Assertions.Expect(page.GetByText("Sign in with Microsoft")).ToBeVisibleAsync(
            new() { Timeout = 15_000 });
    }
}
