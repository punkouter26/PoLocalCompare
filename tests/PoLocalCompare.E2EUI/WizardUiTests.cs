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
        // /war-room is a legacy stub that redirects into the wizard at "/", so entering
        // through it also covers that hop.
        var page = await SignedInPageAsync(width, height, "/war-room");

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
