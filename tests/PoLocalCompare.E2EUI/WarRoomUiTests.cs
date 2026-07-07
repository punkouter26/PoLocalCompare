using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// C# Playwright UI smoke tests. Drives a real browser against a running instance.
///
/// Prerequisites (not run in CI per project policy — invoke manually):
///   1. App running at BASE_URL (default https://localhost:5001).
///   2. Browsers installed: `pwsh bin/Debug/net10.0/playwright.ps1 install chromium`.
/// Authentication uses the dev guest bypass via /e2e/seed-auth (sets the BFF session cookie).
/// </summary>
public sealed class WarRoomUiTests : IAsyncLifetime
{
    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("BASE_URL")?.TrimEnd('/') ?? "https://localhost:5001";

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;
    private IBrowserContext _context = null!;

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
        });
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _browser.DisposeAsync();
        _pw.Dispose();
    }

    [Fact]
    public async Task Login_ThenWarRoom_ShowsHeadingAndDisabledCommence()
    {
        var page = await _context.NewPageAsync();

        // Guest sign-in (sets the BFF cookie) then land on the War Room.
        await page.GotoAsync("/e2e/seed-auth?redirect=/war-room");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        await Assertions.Expect(page.Locator(".war-room__title")).ToContainTextAsync("War Room");

        var commence = page.Locator(".war-room__commence-btn");
        await Assertions.Expect(commence).ToBeVisibleAsync();
        await Assertions.Expect(commence).ToBeDisabledAsync();
    }

    [Fact]
    public async Task Unauthenticated_Root_ShowsLogin()
    {
        var page = await _context.NewPageAsync();
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        // The auth gate renders the Login view (Sign in with Microsoft) before any page content.
        await Assertions.Expect(page.GetByText("Sign in with Microsoft")).ToBeVisibleAsync(
            new() { Timeout = 15_000 });
    }
}
