using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Manages GUEST authentication for local/dev/E2E usage, persisting the
/// generated identity in LocalStorage so it survives page refreshes.
/// </summary>
/// <remarks>
/// Pattern: Service Locator via DI (GoF) — clients inject this singleton to
/// read/write the active user identity without coupling to a full OIDC stack.
/// GUEST identities are intentionally limited to non-production mode and are
/// persisted to LocalStorage for development and E2E resilience.
/// </remarks>
public sealed class GuestAuthService
{
    private const string LocalStorageKey = "guest_identity";

    private readonly IJSRuntime _js;
    private string? _cachedIdentity;

    /// <summary>Fires whenever the identity changes (login or logout).</summary>
    public event Action? OnChange;

    public GuestAuthService(IJSRuntime js) => _js = js;

    /// <summary>
    /// Returns the current user display name.
    /// Reads from the in-memory cache first; falls back to LocalStorage.
    /// Returns <c>null</c> when not logged in.
    /// </summary>
    public async ValueTask<string?> GetIdentityAsync()
    {
        if (_cachedIdentity is not null)
            return _cachedIdentity;

        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", LocalStorageKey);
            _cachedIdentity = stored;
            return _cachedIdentity;
        }
        catch
        {
            // LocalStorage unavailable (e.g., SSR pre-render) — return null gracefully.
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the current identity is a GUEST account.
    /// </summary>
    public bool IsGuest => _cachedIdentity?.StartsWith("GUEST", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Creates a new GUEST identity (GUEST + 8-digit random number), persists
    /// it to LocalStorage, and raises <see cref="OnChange"/>.
    /// </summary>
    public async Task LoginAsGuestAsync()
    {
        var number = Random.Shared.Next(10000000, 99999999);
        var identity = $"GUEST{number}";
        await PersistAsync(identity);
    }

    /// <summary>
    /// Clears the current identity from memory and LocalStorage.
    /// </summary>
    public async Task LogoutAsync()
    {
        _cachedIdentity = null;
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", LocalStorageKey);
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
            await _js.InvokeVoidAsync("localStorage.setItem", LocalStorageKey, identity);
        }
        catch { /* ignore if unavailable */ }
        OnChange?.Invoke();
    }
}
