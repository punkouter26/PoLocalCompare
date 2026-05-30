// GoF: Observer — server pushes lobby state changes to all subscribed clients.
using Microsoft.AspNetCore.SignalR;
using PoLocalCompare.Shared.DTOs;
using System.Collections.Concurrent;

namespace PoLocalCompare.Api.Hubs;

/// <summary>
/// Single shared lobby hub. The first player to connect is designated Host.
/// The server is the authoritative source of truth for connection state and
/// start validation — the Host drives UI decisions but cannot bypass server checks.
/// </summary>
/// <remarks>
/// Pattern: Server-Validated Host (standards §14) — host actions are accepted only
/// when the server confirms all preconditions (all players ready, ≥2 players connected).
/// Static shared state is intentional: the app has one lobby room.
/// </remarks>
public sealed class LobbyHub : Hub
{
    private static readonly ConcurrentDictionary<string, string> _connectionToUser
        = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, LobbyPlayerRecord> _players
        = new(StringComparer.Ordinal);

    private static string? _hostUserId;
    private static readonly SemaphoreSlim _stateLock = new(1, 1);
    private const string LobbyGroup = "lobby";

    /// <summary>
    /// Called by the client immediately after connecting. Adds the player to the
    /// shared lobby and broadcasts the updated state.
    /// </summary>
    public async Task JoinLobby(string userId)
    {
        await _stateLock.WaitAsync();
        try
        {
            _connectionToUser[Context.ConnectionId] = userId;
            if (!_players.ContainsKey(userId))
            {
                var isFirst = _players.IsEmpty;
                _players[userId] = new LobbyPlayerRecord(userId, isFirst, false, DateTimeOffset.UtcNow);
                if (isFirst) _hostUserId = userId;
            }
        }
        finally { _stateLock.Release(); }

        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup);
        await BroadcastStateAsync();
    }

    /// <summary>Toggles the calling player's ready state and broadcasts the new lobby state.</summary>
    public async Task ToggleReady(string userId)
    {
        if (_players.TryGetValue(userId, out var record))
        {
            _players[userId] = record with { IsReady = !record.IsReady };
            await BroadcastStateAsync();
        }
    }

    /// <summary>
    /// Host-only: initiates a duel for all connected lobby players.
    /// Server validates host identity and that all players are ready before firing.
    /// </summary>
    public async Task StartDuel(string leftModelId, string rightModelId, string prompt)
    {
        if (!_connectionToUser.TryGetValue(Context.ConnectionId, out var callerId))
            return;

        var state = BuildState();
        if (callerId != _hostUserId || !state.CanStart)
            return;

        await Clients.Group(LobbyGroup).SendAsync("DuelStarting", leftModelId, rightModelId, prompt);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_connectionToUser.TryRemove(Context.ConnectionId, out var userId))
            {
                var stillConnected = _connectionToUser.Values.Any(u =>
                    string.Equals(u, userId, StringComparison.Ordinal));

                if (!stillConnected)
                {
                    _players.TryRemove(userId, out _);

                    if (string.Equals(_hostUserId, userId, StringComparison.Ordinal))
                    {
                        // Promote the next player (earliest join time) to Host.
                        var next = _players.Values
                            .OrderBy(p => p.JoinedAt)
                            .FirstOrDefault();

                        _hostUserId = next?.UserId;
                        if (next is not null)
                            _players[next.UserId] = next with { IsHost = true };
                    }
                }
            }
        }
        finally { _stateLock.Release(); }

        await base.OnDisconnectedAsync(exception);
        await BroadcastStateAsync();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task BroadcastStateAsync()
    {
        var state = BuildState();
        await Clients.Group(LobbyGroup).SendAsync("LobbyStateChanged", state);
    }

    private static LobbyStateDto BuildState()
    {
        var ordered = _players.Values
            .OrderBy(p => p.JoinedAt)
            .Select(p => new LobbyPlayerDto
            {
                UserId = p.UserId,
                IsHost = string.Equals(p.UserId, _hostUserId, StringComparison.Ordinal),
                IsReady = p.IsReady,
                JoinedAt = p.JoinedAt
            })
            .ToList();

        return new LobbyStateDto
        {
            Players = ordered,
            HostUserId = _hostUserId,
            CanStart = ordered.Count >= 2 && ordered.All(p => p.IsReady)
        };
    }

    private sealed record LobbyPlayerRecord(
        string UserId,
        bool IsHost,
        bool IsReady,
        DateTimeOffset JoinedAt);
}
