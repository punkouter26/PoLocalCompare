using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// The theme layer is the one piece of UI whose correctness cannot be asserted from markup
/// alone — it depends on cascade order between a <c>prefers-color-scheme</c> block and the
/// <c>[data-theme]</c> override, plus localStorage persistence across a reload. Only a real
/// browser can tell you whether that actually resolves the way it was written.
/// </summary>
[Trait("Category", "UI")]
public sealed class ThemeUiTests : UiTestBase
{
    private static Task<string> BodyBackgroundAsync(IPage page) =>
        page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");

    private static Task<string?> ThemeAttributeAsync(IPage page) =>
        page.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme')");

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task SystemDark_RendersDarkWithoutAnExplicitChoice(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, colorScheme: ColorScheme.Dark);

        // No stored choice, so the attribute stays off and prefers-color-scheme is in charge.
        Assert.Null(await ThemeAttributeAsync(page));
        Assert.Equal("rgb(0, 0, 0)", await BodyBackgroundAsync(page));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task SystemLight_RendersLightWithoutAnExplicitChoice(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, colorScheme: ColorScheme.Light);

        Assert.Null(await ThemeAttributeAsync(page));
        Assert.Equal("rgb(247, 248, 251)", await BodyBackgroundAsync(page));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Toggle_IsPresentAndLabelled(int width, int height)
    {
        var page = await SignedInPageAsync(width, height);

        await OpenNavIfCollapsedAsync(page);

        var toggle = page.Locator(".nav-theme-toggle");
        await Assertions.Expect(toggle).ToBeVisibleAsync();
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("Theme:"));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Toggle_MeetsTheMinimumTargetSize(int width, int height)
    {
        // SC 2.5.8 requires 24×24 CSS pixels.
        var page = await SignedInPageAsync(width, height);

        await OpenNavIfCollapsedAsync(page);

        var box = await page.Locator(".nav-theme-toggle").BoundingBoxAsync();

        Assert.NotNull(box);
        Assert.True(box!.Width >= 24, $"Toggle width was {box.Width}.");
        Assert.True(box.Height >= 24, $"Toggle height was {box.Height}.");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Toggle_OverridesASystemDarkPreference(int width, int height)
    {
        // The load-bearing case: an explicit Light choice must beat prefers-color-scheme: dark.
        // That only works because the [data-theme] rules are declared after the media block.
        var page = await SignedInPageAsync(width, height, colorScheme: ColorScheme.Dark);

        await OpenNavIfCollapsedAsync(page);
        await page.Locator(".nav-theme-toggle").ClickAsync();   // System → Light

        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        Assert.Equal("rgb(247, 248, 251)", await BodyBackgroundAsync(page));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Toggle_OverridesASystemLightPreference(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, colorScheme: ColorScheme.Light);

        await OpenNavIfCollapsedAsync(page);
        await page.Locator(".nav-theme-toggle").ClickAsync();   // System → Light
        await page.Locator(".nav-theme-toggle").ClickAsync();   // Light  → Dark

        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        Assert.Equal("rgb(0, 0, 0)", await BodyBackgroundAsync(page));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Toggle_CyclesBackToFollowingTheSystem(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, colorScheme: ColorScheme.Dark);
        await OpenNavIfCollapsedAsync(page);

        var toggle = page.Locator(".nav-theme-toggle");

        await toggle.ClickAsync();  // Light
        await toggle.ClickAsync();  // Dark
        await toggle.ClickAsync();  // back to System

        Assert.Null(await ThemeAttributeAsync(page));
        Assert.Equal("rgb(0, 0, 0)", await BodyBackgroundAsync(page));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Toggle_ChoiceSurvivesAReload(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, colorScheme: ColorScheme.Dark);

        await OpenNavIfCollapsedAsync(page);
        await page.Locator(".nav-theme-toggle").ClickAsync();   // System → Light
        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Theme_IsAppliedBeforeFirstPaint(int width, int height)
    {
        // theme.js is loaded in <head> precisely so the attribute is already set by the time
        // the document finishes parsing — otherwise every navigation flashes the wrong palette.
        var page = await NewPageAsync(width, height, ColorScheme.Dark);
        await page.GotoAsync("/e2e/seed-auth?redirect=/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        await OpenNavIfCollapsedAsync(page);
        await page.Locator(".nav-theme-toggle").ClickAsync();   // store an explicit Light

        await page.GotoAsync("/leaderboard");
        // DOMContentLoaded, not NetworkIdle: the assertion is that the attribute is present
        // before the app has finished booting, not merely afterwards.
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        Assert.Equal("light", await ThemeAttributeAsync(page));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task RadzenStylesheet_TracksTheChosenTheme(int width, int height)
    {
        // Radzen ships one stylesheet per theme rather than custom properties, so the <link>
        // has to be swapped alongside the attribute or its grid stays dark on a light page.
        var page = await SignedInPageAsync(width, height, colorScheme: ColorScheme.Dark);

        await OpenNavIfCollapsedAsync(page);
        await page.Locator(".nav-theme-toggle").ClickAsync();   // System → Light

        var href = await page.EvaluateAsync<string>(
            "() => document.getElementById('radzen-theme').getAttribute('href')");
        Assert.Contains("default.css", href);
    }
}
