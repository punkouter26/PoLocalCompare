using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// The theme layer is the one piece of UI whose correctness cannot be asserted from markup
/// alone — it depends on cascade order between a <c>prefers-color-scheme</c> block and the
/// <c>[data-theme]</c> override, plus localStorage persistence across a reload. Only a real
/// browser can tell you whether that actually resolves the way it was written.
///
/// Trimmed to two methods in the 2026-08-13 prune. The eight it replaced all drove the same
/// toggle; walking the cycle once asserts every state it used to check one state per method.
/// </summary>
[Trait("Category", "UI")]
public sealed class ThemeUiTests : UiTestBase
{
    private const string DarkBackground = "rgb(0, 0, 0)";
    private const string LightBackground = "rgb(247, 248, 251)";

    private static Task<string> BodyBackgroundAsync(IPage page) =>
        page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");

    private static Task<string?> ThemeAttributeAsync(IPage page) =>
        page.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme')");

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Toggle_CyclesSystemThenOverridesThePreferenceInBothDirections(int width, int height)
    {
        // The load-bearing case: an explicit choice must beat prefers-color-scheme. That only
        // works because the [data-theme] rules are declared after the media block — which is
        // why CLAUDE.md requires those blocks to stay last in app.css.
        var page = await SignedInPageAsync(width, height, colorScheme: ColorScheme.Dark);

        // Starting state: no stored choice, so the attribute stays off and the media query rules.
        Assert.Null(await ThemeAttributeAsync(page));
        Assert.Equal(DarkBackground, await BodyBackgroundAsync(page));

        await OpenNavIfCollapsedAsync(page);
        var toggle = page.Locator(".navmenu__theme-toggle");

        await Assertions.Expect(toggle).ToHaveAttributeAsync(
            "aria-label", new System.Text.RegularExpressions.Regex("Theme:"));

        // SC 2.5.8 requires 24×24 CSS pixels.
        var box = await toggle.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Width >= 24 && box.Height >= 24, $"Toggle was {box.Width}×{box.Height}.");

        // System → Light: the explicit choice overriding a dark OS preference.
        await toggle.ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        Assert.Equal(LightBackground, await BodyBackgroundAsync(page));

        // Light → Dark: the override works in the other direction too.
        await toggle.ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        Assert.Equal(DarkBackground, await BodyBackgroundAsync(page));

        // Dark → System: the attribute comes off and the media query is back in charge.
        await toggle.ClickAsync();
        Assert.Null(await ThemeAttributeAsync(page));
        Assert.Equal(DarkBackground, await BodyBackgroundAsync(page));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Theme_SurvivesAReloadAndIsAppliedBeforeFirstPaint(int width, int height)
    {
        // theme.js is loaded in <head> precisely so the attribute is already set by the time
        // the document finishes parsing — otherwise every navigation flashes the wrong palette.
        var page = await NewPageAsync(width, height, ColorScheme.Dark);
        await page.GotoAsync(SeedAuthUrl("/"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        await OpenNavIfCollapsedAsync(page);
        await page.Locator(".navmenu__theme-toggle").ClickAsync();   // store an explicit Light

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");

        await page.GotoAsync("/leaderboard");
        // DOMContentLoaded, not NetworkIdle: the assertion is that the attribute is present
        // before the app has finished booting, not merely afterwards.
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        Assert.Equal("light", await ThemeAttributeAsync(page));

        // And the tokens actually reach a rendered surface: the leaderboard grid is a plain
        // .po-table styled from the design tokens, so its computed colour must track the choice
        // rather than a stylesheet <link> being swapped (which is how this worked under Radzen).
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });
        var header = page.Locator(".po-table th").First;
        await Assertions.Expect(header).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var lightColor = await header.EvaluateAsync<string>("el => getComputedStyle(el).color");

        await OpenNavIfCollapsedAsync(page);
        await page.Locator(".navmenu__theme-toggle").ClickAsync();   // Light → Dark
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        var darkColor = await header.EvaluateAsync<string>("el => getComputedStyle(el).color");

        Assert.NotEqual(lightColor, darkColor);
    }
}
