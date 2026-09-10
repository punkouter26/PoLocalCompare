using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// The commence → wait → verdict sequence every integration suite was re-implementing.
/// </summary>
/// <remarks>
/// <para>
/// This exists because four classes each had their own <c>RunDuelAsync</c> that commenced a
/// duel and posted the verdict on the very next line. Duel execution is asynchronous — the API
/// returns 202 after enqueueing, and <c>DuelExecutionService</c> writes the two result rows
/// later, on <c>BackgroundTaskService</c>'s single consumer — so whether the rows existed by
/// the time the verdict arrived came down to whether a local HTTP round trip beat a container
/// write to Azurite.
/// </para>
/// <para>
/// When it lost that race, <c>RecordVerdictHandler.GuardAgainstNoEvidenceAsync</c> correctly
/// refused with 409 "This duel is still running — a model has not reported a result yet", and
/// the test failed for a reason that had nothing to do with what it was asserting. Measured on
/// 2026-09-10 this cost 7–9 of the 50 integration cases per run, varying run to run, in
/// ModelProfileTests, LeaderboardTests, DuelsEndpointTests, KillListTests and
/// VerdictWriteOrderTests. It reproduced identically at a clean HEAD, so it was never caused by
/// a change in the working tree — it is inherent to the pattern.
/// </para>
/// <para>
/// The guard is a deliberate product invariant and must stay: ELO must never move on missing
/// evidence. So the tests wait for the evidence instead of the invariant being relaxed.
/// </para>
/// </remarks>
internal static class DuelTestFlow
{
    private const string DefaultPrompt = "Build an HTML app.";

    /// <summary>Polls until both models have reported, which is what a verdict requires.</summary>
    public static async Task WaitForBothResultsAsync(HttpClient client, string duelId, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            var response = await client.GetAsync($"/api/duels/{duelId}");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("results", out var results)
                    && results.ValueKind == JsonValueKind.Array
                    && results.GetArrayLength() >= 2)
                {
                    return;
                }
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"Duel '{duelId}' did not report both results within {timeoutMs} ms. " +
                    "Duel execution runs on the background queue — a timeout here means it never ran.");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Commences a duel, waits for both results, then records <paramref name="verdictSide"/>
    /// ("Left" or "Right"). Returns the duel id so callers can assert on it afterwards.
    /// </summary>
    public static async Task<string> RunDuelAsync(
        HttpClient client,
        string leftId,
        string rightId,
        string verdictSide,
        string prompt = DefaultPrompt)
    {
        var duelId = await CommenceAsync(client, leftId, rightId, prompt);
        await WaitForBothResultsAsync(client, duelId);

        var verdict = await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = verdictSide });
        verdict.EnsureSuccessStatusCode();

        return duelId;
    }

    /// <summary>Commences a duel without recording a verdict, and returns its id.</summary>
    public static async Task<string> CommenceAsync(
        HttpClient client,
        string leftId,
        string rightId,
        string prompt = DefaultPrompt)
    {
        var commence = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = prompt,
        });
        commence.EnsureSuccessStatusCode();

        return (await commence.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("duelId").GetString()!;
    }
}
