using System.Text.Json;
using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// The Arena — the screen a duel actually ends on. No UI test covered it before, which is how
/// it came to throw on every load: the page derived its DuelId in OnParametersSet, but Blazor
/// runs OnInitializedAsync first, so the initial fetch went to /api/duels/ with an empty id,
/// matched the list route, and failed deserializing an array into one DuelDto.
///
/// This asserts the page mounts, shows duel content, and exposes the Lab Report anchor, which is
/// what that bug broke. See <see cref="UiTestBase"/> for prerequisites; not run in CI.
/// </summary>
[Trait("Category", "UI")]
public sealed class ArenaUiTests : UiTestBase
{
    /// <summary>
    /// Reads a real duel id from the API rather than seeding one: the Arena needs a duel that
    /// already has results, and creating one here would run live inference.
    /// </summary>
    private static async Task<string?> FirstDuelIdAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            "async () => { const r = await fetch('/api/duels?limit=1'); return r.ok ? await r.text() : ''; }");

        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
            ? doc.RootElement[0].GetProperty("duelId").GetString()
            : null;
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Arena_LoadsTheRequestedDuel_AndTheArchiveOffersTheLabReport(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, "/archive");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        var duelId = await FirstDuelIdAsync(page);
        // Stated rather than silently skipped: a green run that asserted nothing would be
        // worse than a red one that says what is missing.
        Assert.True(duelId is not null, "No duels recorded — run a duel or a tournament before this suite.");

        // The export slice has no UI of its own, so the anchor is the only thing making that
        // endpoint reachable — it just lives on the Archive row rather than in the Arena, where
        // the export disclosure was dropped in the 2026-08-23 control-count pass. Asserted here
        // while the Archive is on screen, before navigating away to the duel itself.
        var report = page.Locator($"a[href='/api/duels/{duelId}/report']");
        await Assertions.Expect(report).ToHaveAttributeAsync(
            "download", $"lab-report-{duelId}.html", new() { Timeout = width < 768 ? 30_000 : 10_000 });

        await page.GotoAsync($"/arena/{duelId}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        var timeout = width < 768 ? 30_000 : 10_000;

        // The heading proving the component mounted...
        await Assertions.Expect(page.Locator(".arena__title")).ToBeVisibleAsync(new() { Timeout = timeout });

        // ...and no error boundary, which is what the empty-id fetch used to trip.
        await Assertions.Expect(page.Locator(".dev-error-panel")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".arena__error")).Not.ToBeVisibleAsync();

        // The prompt only renders once the duel itself deserialized, so it is the real signal
        // that the right id reached the API.
        await Assertions.Expect(page.Locator(".arena__prompt-label")).ToBeVisibleAsync(new() { Timeout = timeout });
    }
}
