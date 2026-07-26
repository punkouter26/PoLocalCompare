using Microsoft.Playwright;

namespace PoLocalCompare.Tests.E2E.Ui;

/// <summary>TEMPORARY diagnostic — reproduces the "prompt box is read-only" report.</summary>
[Trait("Category", "UI")]
public sealed class PromptInputProbe
{
    [Fact]
    public async Task Probe()
    {
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var ctx = await browser.NewContextAsync(new()
        {
            BaseURL = "https://localhost:5001",
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        var page = await ctx.NewPageAsync();

        var console = new List<string>();
        page.Console += (_, m) => console.Add($"[{m.Type}] {m.Text}");
        page.PageError += (_, e) => console.Add($"[pageerror] {e}");

        await page.GotoAsync("/e2e/seed-auth?redirect=/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 60_000 });

        var ta = page.Locator(".wizard__textarea");
        Console.WriteLine($"count={await ta.CountAsync()}");
        Console.WriteLine($"visible={await ta.IsVisibleAsync()} editable={await ta.IsEditableAsync()} enabled={await ta.IsEnabledAsync()}");
        Console.WriteLine("attrs: readonly=" + await ta.GetAttributeAsync("readonly")
            + " disabled=" + await ta.GetAttributeAsync("disabled"));

        // What element actually receives a click at the textarea's centre?
        var box = await ta.BoundingBoxAsync();
        if (box is not null)
        {
            var hit = await page.EvaluateAsync<string>(
                "p => { const e = document.elementFromPoint(p.x, p.y); return e ? e.tagName + '.' + e.className : 'none'; }",
                new { x = box.X + box.Width / 2, y = box.Y + box.Height / 2 });
            Console.WriteLine($"elementFromPoint => {hit}");
        }

        try
        {
            await ta.ClickAsync(new() { Timeout = 5000 });
            await ta.TypeAsync("hello world", new() { Delay = 20 });
        }
        catch (Exception ex)
        {
            Console.WriteLine("TYPE FAILED: " + ex.Message);
        }

        Console.WriteLine($"value after typing = '{await ta.InputValueAsync()}'");
        Console.WriteLine($"counter = '{await page.Locator(".wizard__textarea-counter").First.InnerTextAsync()}'");

        Console.WriteLine("---- console ----");
        foreach (var line in console.TakeLast(40)) Console.WriteLine(line);
    }
}
