using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// The compare wizard at "/" — the app's primary journey. See <see cref="UiTestBase"/> for the
/// prerequisites; this suite is not run in CI.
/// </summary>
[Trait("Category", "UI")]
public sealed class WizardUiTests : UiTestBase
{
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Login_ThenWizard_ShowsHeadingAndDisabledCompare(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, "/");

        // Headless Chromium on a narrow viewport is the slowest first paint in this matrix:
        // mobile network-idle takes longer and Blazor WASM download + boot adds seconds on top.
        // The default 5 s assertion timeout is too tight there; the same code on desktop has
        // headroom to spare. Generous enough to be reliable, narrow enough to still fail on a
        // genuine regression (a heading that does not render at all will not appear in 30 s).
        var timeout = width < 768 ? 30_000 : 10_000;
        await Assertions.Expect(page.Locator(".wizard__title")).ToContainTextAsync(
            "Compare two models", new() { Timeout = timeout });

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

        // The auth gate renders the Login view before any page content.
        await Assertions.Expect(page.GetByText("Sign in with Microsoft")).ToBeVisibleAsync(
            new() { Timeout = 15_000 });
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Wizard_RendersTheModelGrid(int width, int height)
    {
        var page = await SignedInPageAsync(width, height);

        await Assertions.Expect(page.Locator(".wizard__grid").First).ToBeVisibleAsync(
            new() { Timeout = 20_000 });
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task ModelCards_AreKeyboardFocusable(int width, int height)
    {
        // SC 2.1.1: the card is a custom toggle button, so it must be reachable by keyboard.
        var page = await SignedInPageAsync(width, height);

        var card = page.Locator(".model-card").First;
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 20_000 });

        await Assertions.Expect(card).ToHaveAttributeAsync("role", "button");
        await Assertions.Expect(card).ToHaveAttributeAsync("tabindex", "0");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task ModelCards_ExposeTheirSelectedState(int width, int height)
    {
        // SC 4.1.2 Name, Role, Value — aria-pressed has to track the visual selection.
        var page = await SignedInPageAsync(width, height);

        var card = page.Locator(".model-card").First;
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 20_000 });

        // Lowercase "false": the value is now rendered from a string property. Binding the
        // bool directly made Blazor omit the attribute entirely when unselected.
        await Assertions.Expect(card).ToHaveAttributeAsync("aria-pressed", "false");
    }
}
