using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// Header contract, routing, and the accessibility affordances that only exist at runtime
/// (focus order, the skip link, target sizes).
/// </summary>
[Trait("Category", "UI")]
public sealed class NavigationUiTests : UiTestBase
{
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Header_ShowsBrandingOnTheLeftAndSessionOnTheRight(int width, int height)
    {
        // Standards §4 layout contract: Left (branding) | Centre (actions) | Right (session).
        // On a narrow viewport the bar collapses into a vertical drawer, so the contract
        // becomes ordering rather than horizontal position — assert the axis that applies.
        var page = await SignedInPageAsync(width, height);
        await OpenNavIfCollapsedAsync(page);

        var brand = await page.Locator(".navmenu__brand").BoundingBoxAsync();
        var auth = await page.Locator(".navmenu__auth").BoundingBoxAsync();

        Assert.NotNull(brand);
        Assert.NotNull(auth);
        if (width < 768)
            Assert.True(brand!.Y < auth!.Y, "In the drawer, branding should sit above the session controls.");
        else
            Assert.True(brand!.X < auth!.X, "Branding should sit left of the session controls.");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Header_ItemsDoNotOverlapOrLeaveTheViewport(int width, int height)
    {
        // The header's items are nowrap. Any that is allowed to shrink is squeezed narrower
        // than its own label, the label spills out of the box, and the next item paints on top
        // of it — which is how the two ticker chips ended up printed over each other. Nothing
        // else in the suite looks at geometry, so this is the only place that can catch it.
        // Note the live ticker only takes part when duels are actually running.
        var page = await SignedInPageAsync(width, height);
        await OpenNavIfCollapsedAsync(page);

        var problems = await page.EvaluateAsync<string[]>(
            """
            () => {
                const sel = '.navmenu__brand, .navmenu__link, .navmenu__ticker-pill,'
                          + ' .navmenu__ticker-latest, .navmenu__user-badge, .navmenu__auth > .po-btn';
                const els = [...document.querySelectorAll(sel)].filter(e => e.offsetParent !== null);
                const label = e => (e.textContent || e.className).trim().slice(0, 24);
                const bad = [];
                for (let i = 0; i < els.length; i++) {
                    const a = els[i].getBoundingClientRect();
                    if (a.right > window.innerWidth + 1)
                        bad.push('clipped by the viewport: ' + label(els[i]));
                    for (let j = i + 1; j < els.length; j++) {
                        if (els[i].contains(els[j]) || els[j].contains(els[i])) continue;
                        const b = els[j].getBoundingClientRect();
                        if (Math.min(a.right, b.right) - Math.max(a.left, b.left) > 1 &&
                            Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 1)
                            bad.push('overlap: ' + label(els[i]) + ' / ' + label(els[j]));
                    }
                }
                return bad;
            }
            """);

        Assert.True(problems.Length == 0, string.Join(" | ", problems));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Header_ShowsTheSignedInIdentity(int width, int height)
    {
        var page = await SignedInPageAsync(width, height);
        await OpenNavIfCollapsedAsync(page);

        await Assertions.Expect(page.Locator(".navmenu__user-badge")).ToBeVisibleAsync();
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task SkipLink_IsTheFirstTabStop(int width, int height)
    {
        // SC 2.4.1 Bypass Blocks — the link is visually hidden until focused.
        var page = await SignedInPageAsync(width, height);

        // Blazor's <FocusOnNavigate Selector="h1"> parks focus on the heading after routing,
        // so tabbing from there lands *past* the skip link. Reset to the document start —
        // which is where a person arriving at the page actually begins.
        await page.EvaluateAsync("() => document.activeElement?.blur()");
        await page.Keyboard.PressAsync("Tab");

        var focusedClass = await page.EvaluateAsync<string>(
            "() => document.activeElement?.className ?? ''");
        Assert.Contains("skip-link", focusedClass);
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task SkipLink_MovesFocusToMainContent(int width, int height)
    {
        var page = await SignedInPageAsync(width, height);

        await page.EvaluateAsync("() => document.activeElement?.blur()");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");

        var focusedId = await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''");
        Assert.Equal("main-content", focusedId);
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Leaderboard_IsReachableAndRendersItsHeading(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, "/leaderboard");

        await Assertions.Expect(page.Locator(".leaderboard__title")).ToBeVisibleAsync(
            new() { Timeout = 15_000 });
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Archive_IsReachableAndRendersItsHeading(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, "/archive");

        await Assertions.Expect(page.Locator(".archive__title")).ToBeVisibleAsync(
            new() { Timeout = 15_000 });
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task UnknownRoute_RendersTheNotFoundPage(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, "/no-such-page");

        // Assert on the page's own element: body text also contains the skip link and nav, so
        // a substring match there passes for the wrong reasons and is slow to settle.
        await Assertions.Expect(page.Locator(".not-found__title")).ToBeVisibleAsync(
            new() { Timeout = 20_000 });
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Page_DoesNotScrollHorizontally(int width, int height)
    {
        // Mobile portrait is the primary target; a sheared right edge is the classic failure.
        var page = await SignedInPageAsync(width, height);

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");

        Assert.False(overflows, "The page overflows horizontally.");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task NoConsoleErrorsOnFirstLoad(int width, int height)
    {
        var page = await NewPageAsync(width, height);
        var errors = new List<string>();
        page.Console += (_, msg) =>
        {
            if (msg.Type == "error") errors.Add(msg.Text);
        };

        await page.GotoAsync(SeedAuthUrl("/"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        Assert.True(errors.Count == 0, "Console errors: " + string.Join(" | ", errors));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task DiagPage_RendersWithoutTheWasmClient(int width, int height)
    {
        // /diag is server-rendered on purpose: it is the page you open when the client is
        // the thing that is broken, and index.html's boot fallback links to it.
        var page = await NewPageAsync(width, height);
        await page.GotoAsync("/diag");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync("Diagnostics");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task DiagPage_IsNotLinkedFromTheApp(int width, int height)
    {
        // Standards: /health and /diag exist but must have no UI links.
        var page = await SignedInPageAsync(width, height);

        var diagLinks = await page.Locator("nav a[href='/diag'], nav a[href='/health']").CountAsync();

        Assert.Equal(0, diagLinks);
    }
}
