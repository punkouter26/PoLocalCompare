using System.Text.Json;
using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>A prompt the user has actually run, with when they last ran it.</summary>
public sealed record RecentPrompt(string Text, DateTimeOffset UsedAt)
{
    /// <summary>Single-line, length-capped rendering for the recall chips.</summary>
    public string Summary =>
        Text.Length <= 70 ? Text : Text[..70].TrimEnd() + "…";
}

/// <summary>
/// Remembers prompts across sessions in <c>localStorage</c>.
/// </summary>
/// <remarks>
/// Client-only on purpose. Prompts are drafting material, not duel records — the duel itself is
/// already persisted server-side — so storing them per-browser avoids adding a write path and a
/// per-user partition for something that is really editor state. It also means a guest session
/// keeps its drafts, which a server-side store keyed on identity would not.
/// </remarks>
public sealed class PromptHistoryService(IJSRuntime js)
{
    private const string StorageKey = "polocalcompare.recentPrompts";
    private const int MaxEntries = 10;

    /// <summary>Ignore trivial fragments — the wizard binds on every keystroke.</summary>
    private const int MinLength = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<IReadOnlyList<RecentPrompt>> GetAsync()
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrWhiteSpace(raw)) return [];

            var entries = JsonSerializer.Deserialize<List<RecentPrompt>>(raw, SerializerOptions);
            return entries?.Where(e => !string.IsNullOrWhiteSpace(e.Text)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or JSException or InvalidOperationException)
        {
            // Corrupted, absent, or storage blocked (private mode, disabled cookies). Prompt
            // recall is a convenience — it must never take the Compare page down with it.
            return [];
        }
    }

    /// <summary>
    /// Records a prompt as most-recently-used. Re-running an existing prompt moves it to the
    /// front rather than adding a duplicate, so the list stays a set of distinct prompts.
    /// </summary>
    public async Task<IReadOnlyList<RecentPrompt>> RememberAsync(string? prompt)
    {
        var trimmed = prompt?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < MinLength)
            return await GetAsync();

        var existing = (await GetAsync()).ToList();
        existing.RemoveAll(e => string.Equals(e.Text, trimmed, StringComparison.Ordinal));
        existing.Insert(0, new RecentPrompt(trimmed, DateTimeOffset.UtcNow));

        var trimmedList = existing.Take(MaxEntries).ToList();
        await WriteAsync(trimmedList);
        return trimmedList;
    }

    public async Task<IReadOnlyList<RecentPrompt>> RemoveAsync(string text)
    {
        var existing = (await GetAsync()).ToList();
        existing.RemoveAll(e => string.Equals(e.Text, text, StringComparison.Ordinal));
        await WriteAsync(existing);
        return existing;
    }

    public async Task ClearAsync()
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        }
        catch (JSException)
        {
        }
    }

    private async Task WriteAsync(List<RecentPrompt> entries)
    {
        try
        {
            await js.InvokeVoidAsync(
                "localStorage.setItem",
                StorageKey,
                JsonSerializer.Serialize(entries, SerializerOptions));
        }
        catch (JSException)
        {
            // Quota exceeded or storage unavailable — the in-memory list the caller holds is
            // still correct for this session.
        }
    }
}
