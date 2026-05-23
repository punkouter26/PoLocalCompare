using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Manages GUEST authentication for local/dev/E2E usage, persisting the
/// generated identity in SessionStorage so it survives page refreshes.
/// </summary>
/// <remarks>
/// Pattern: Service Locator via DI (GoF) — clients inject this singleton to
/// read/write the active user identity without coupling to a full OIDC stack.
/// GUEST identities are intentionally ephemeral (SessionStorage, not LocalStorage)
/// and are cleared when the browser tab closes.
/// </remarks>
public sealed class GuestAuthService
{
    private const string SessionKey = "guest_identity";

    private readonly IJSRuntime _js;
    private string? _cachedIdentity;

    /// <summary>Fires whenever the identity changes (login or logout).</summary>
    public event Action? OnChange;

    public GuestAuthService(IJSRuntime js) => _js = js;

    /// <summary>
    /// Returns the current user display name.
    /// Reads from the in-memory cache first; falls back to SessionStorage.
    /// Returns <c>null</c> when not logged in.
    /// </summary>
    public async ValueTask<string?> GetIdentityAsync()
    {
        if (_cachedIdentity is not null)
            return _cachedIdentity;

        try
        {
            var stored = await _js.InvokeAsync<string?>("sessionStorage.getItem", SessionKey);
            _cachedIdentity = stored;
            return _cachedIdentity;
        }
        catch
        {
            // SessionStorage unavailable (e.g., SSR pre-render) — return null gracefully.
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the current identity is a GUEST account.
    /// </summary>
    public bool IsGuest => _cachedIdentity?.StartsWith("GUEST", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Creates a new GUEST identity (GUEST + 4-digit random number), persists
    /// it to SessionStorage, and raises <see cref="OnChange"/>.
    /// </summary>
    public async Task LoginAsGuestAsync()
    {
        var number = Random.Shared.Next(1000, 9999);
        var identity = $"GUEST{number}";
        await PersistAsync(identity);
    }

    /// <summary>
    /// Clears the current identity from memory and SessionStorage.
    /// </summary>
    public async Task LogoutAsync()
    {
        _cachedIdentity = null;
        try
        {
            await _js.InvokeVoidAsync("sessionStorage.removeItem", SessionKey);
        }
        catch { /* ignore if unavailable */ }
        OnChange?.Invoke();
    }

    /// <summary>
    /// Sets a Microsoft OAuth-sourced email as the active identity.
    /// </summary>
    public async Task LoginWithEmailAsync(string email)
    {
        await PersistAsync(email);
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private async Task PersistAsync(string identity)
    {
        _cachedIdentity = identity;
        try
        {
            await _js.InvokeVoidAsync("sessionStorage.setItem", SessionKey, identity);
        }
        catch { /* ignore if unavailable */ }
        OnChange?.Invoke();
    }
}
