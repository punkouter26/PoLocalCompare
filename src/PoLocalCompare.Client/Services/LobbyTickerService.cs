using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// One app-wide SignalR connection to the lobby group, feeding the nav-bar activity ticker.
/// </summary>
/// <remarks>
/// Registered scoped, which in a WebAssembly host means one instance for the whole app session —
/// so the connection survives navigation instead of being torn down and rebuilt per page. The
/// live counters are derived from events observed while connected, seeded once from the duel
/// list so a freshly-opened tab does not claim there is nothing pending when there is.
/// </remarks>
public sealed class LobbyTickerService(HttpClient http) : IAsyncDisposable
{
    /// <summary>Ticker depth. It is a glance surface — older entries belong in the Archive.</summary>
    private const int MaxEvents = 12;

    private readonly List<LobbyEventDto> _events = [];
    private readonly HashSet<DuelId> _running = [];
    private readonly HashSet<DuelId> _awaitingVerdict = [];

    private HubConnection? _connection;
    private Task? _startTask;

    /// <summary>Raised on every change; the UI re-renders from the properties below.</summary>
    public event Action? OnChanged;

    public IReadOnlyList<LobbyEventDto> Events => _events;

    /// <summary>Duels currently generating.</summary>
    public int RunningCount => _running.Count;

    /// <summary>Finished duels with no verdict yet — the ones worth clicking into.</summary>
    public int AwaitingVerdictCount => _awaitingVerdict.Count;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Idempotent: every component that wants the ticker calls this, and only the first
    /// actually connects. Never throws — the ticker is ambient, and the nav bar must render
    /// whether or not the hub is reachable.
    /// </summary>
    public Task EnsureStartedAsync(string baseUrl) => _startTask ??= StartAsync(baseUrl);

    private async Task StartAsync(string baseUrl)
    {
        await SeedPendingAsync();

        try
        {
            _connection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/duel")
                .WithAutomaticReconnect()
                .Build();

            _connection.On<LobbyEventDto>("LobbyEvent", Apply);

            // A reconnect drops group membership, so rejoining is part of reconnecting.
            _connection.Reconnected += async _ =>
            {
                try
                {
                    await _connection.InvokeAsync("JoinLobby");
                }
                catch (Exception)
                {
                    // Next reconnect will try again.
                }
                OnChanged?.Invoke();
            };

            await _connection.StartAsync();
            await _connection.InvokeAsync("JoinLobby");
            OnChanged?.Invoke();
        }
        catch (Exception)
        {
            // Hub unreachable (offline, auth expired, server restarting). The seeded pending
            // count still renders; live events simply will not arrive.
            _connection = null;
        }
    }

    /// <summary>
    /// Seeds the "awaiting verdict" count from duels that finished before this tab existed.
    /// Without it the badge would read zero on every fresh load regardless of the real backlog.
    /// </summary>
    private async Task SeedPendingAsync()
    {
        try
        {
            var recent = await http.GetFromJsonAsync<IReadOnlyList<DuelSummaryDto>>("/api/duels?limit=25");
            if (recent is null) return;

            foreach (var duel in recent.Where(d => d.Verdict == DuelVerdict.Pending && d.CompletedAt.HasValue))
                _awaitingVerdict.Add(duel.DuelId);

            OnChanged?.Invoke();
        }
        catch (Exception)
        {
            // Not signed in yet, or the API is not up. The ticker degrades to live-only.
        }
    }

    private void Apply(LobbyEventDto evt)
    {
        switch (evt.Kind)
        {
            case LobbyEventKind.DuelStarted:
                _running.Add(evt.DuelId);
                break;

            case LobbyEventKind.DuelCompleted:
                _running.Remove(evt.DuelId);
                _awaitingVerdict.Add(evt.DuelId);
                break;

            case LobbyEventKind.VerdictRecorded:
                // A verdict can land for a duel this tab never saw start — clearing both sets
                // unconditionally keeps the counters from drifting upward over a long session.
                _running.Remove(evt.DuelId);
                _awaitingVerdict.Remove(evt.DuelId);
                break;
        }

        _events.Insert(0, evt);
        if (_events.Count > MaxEvents) _events.RemoveRange(MaxEvents, _events.Count - MaxEvents);

        OnChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
