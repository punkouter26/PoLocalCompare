using System.Text.Json;
using Microsoft.JSInterop;
using PoLocalCompare.Shared.Blind;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Owns blind mode's browser state: the toggle, which duels were started blind, and the votes
/// the user cast while blind.
/// </summary>
/// <remarks>
/// Deliberately client-only, for the same reason <see cref="PromptHistoryService"/> is. Blindness
/// is presentational — the duel, its results and its verdict are identical either way — so it
/// needs no schema change and no per-user partition. Keeping it per-browser also gives the right
/// behaviour for a shared Arena link: the recipient sees the model names, because the duel was
/// never blind for <em>them</em>. The aggregation itself lives in <see cref="BlindPickLedger"/>
/// so the unit tier can test it; this class is the interop shell around it.
///
/// Every read swallows its own failures and answers "not blind". Storage can be unavailable
/// (private mode, blocked cookies) and a duel that silently stays un-masked is a far better
/// outcome than an Arena that fails to render.
/// </remarks>
public sealed class BlindModeService(IJSRuntime js)
{
    private const string EnabledKey = "polocalcompare.blindMode";
    private const string BlindDuelsKey = "polocalcompare.blindDuels";
    private const string VotesKey = "polocalcompare.blindVotes";

    /// <summary>
    /// How many duel ids stay marked blind. Only duels still being looked at matter — once a
    /// duel is revealed the flag has done its job — so this is a small rolling window rather
    /// than a permanent record.
    /// </summary>
    private const int MaxTrackedDuels = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── The toggle ────────────────────────────────────────────────────────────

    public async Task<bool> GetEnabledAsync()
    {
        var raw = await ReadAsync(EnabledKey);
        return string.Equals(raw, "true", StringComparison.Ordinal);
    }

    public async Task SetEnabledAsync(bool enabled) =>
        await WriteAsync(EnabledKey, enabled ? "true" : "false");

    // ── Per-duel blindness ────────────────────────────────────────────────────

    /// <summary>
    /// Marks a duel blind. Called after the duel exists but before the Arena opens, so a reload
    /// mid-generation stays masked instead of revealing the names on the way back.
    /// </summary>
    public async Task MarkBlindAsync(DuelId duelId)
    {
        var tracked = await GetBlindDuelsAsync();
        if (tracked.Contains(duelId.Value, StringComparer.Ordinal)) return;

        var updated = new List<string>(tracked.Count + 1) { duelId.Value };
        updated.AddRange(tracked);
        if (updated.Count > MaxTrackedDuels) updated = updated[..MaxTrackedDuels];

        await WriteAsync(BlindDuelsKey, JsonSerializer.Serialize(updated, SerializerOptions));
    }

    public async Task<bool> IsBlindAsync(DuelId duelId)
    {
        var tracked = await GetBlindDuelsAsync();
        return tracked.Contains(duelId.Value, StringComparer.Ordinal);
    }

    private async Task<List<string>> GetBlindDuelsAsync()
    {
        try
        {
            var raw = await ReadAsync(BlindDuelsKey);
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return JsonSerializer.Deserialize<List<string>>(raw, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ── The vote ledger ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BlindVote>> GetVotesAsync()
    {
        try
        {
            var raw = await ReadAsync(VotesKey);
            if (string.IsNullOrWhiteSpace(raw)) return [];

            var votes = JsonSerializer.Deserialize<List<BlindVote>>(raw, SerializerOptions);
            // A vote missing either side is unusable for a tally and would show as a blank row.
            return votes?.Where(v => !v.WinnerModelId.IsEmpty && !v.LoserModelId.IsEmpty).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Records a blind pick. Only the user's own votes belong here — an AI-judged duel means
    /// nobody expressed a preference, so the Arena does not call this for one.
    /// </summary>
    public async Task<IReadOnlyList<BlindVote>> RecordVoteAsync(BlindVote vote)
    {
        var updated = BlindPickLedger.Append(await GetVotesAsync(), vote);
        await WriteAsync(VotesKey, JsonSerializer.Serialize(updated, SerializerOptions));
        return updated;
    }

    public async Task ClearVotesAsync() => await RemoveAsync(VotesKey);

    // ── localStorage plumbing ─────────────────────────────────────────────────

    private async Task<string?> ReadAsync(string key)
    {
        try
        {
            return await js.InvokeAsync<string?>("localStorage.getItem", key);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task WriteAsync(string key, string value)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", key, value);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Quota or unavailable storage. The caller's in-memory state is still right for
            // this session; blindness simply will not survive a reload.
        }
    }

    private async Task RemoveAsync(string key)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", key);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
        }
    }
}
