using Microsoft.AspNetCore.SignalR.Client;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Connects to /hubs/lobby and exposes typed SignalR events for lobby coordination.
/// </summary>
/// <remarks>
/// Pattern: Facade — wraps the raw HubConnection API so that Lobby.razor interacts
/// with strongly-typed events instead of string-keyed SendAsync calls.
/// </remarks>
public sealed class SignalRLobbyClient : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<LobbyStateDto>? OnLobbyStateChanged;
    public event Action<string, string, string>? OnDuelStarting;

    public HubConnectionState State =>
        _connection?.State ?? HubConnectionState.Disconnected;

    public async Task ConnectAsync(string baseUrl, string userId, CancellationToken cancellationToken = default)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/lobby")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<LobbyStateDto>("LobbyStateChanged", state =>
            OnLobbyStateChanged?.Invoke(state));

        _connection.On<string, string, string>("DuelStarting", (left, right, prompt) =>
            OnDuelStarting?.Invoke(left, right, prompt));

        await _connection.StartAsync(cancellationToken);
        await _connection.InvokeAsync("JoinLobby", userId, cancellationToken);
    }

    public async Task ToggleReadyAsync(string userId)
    {
        if (_connection is null) return;
        await _connection.InvokeAsync("ToggleReady", userId);
    }

    public async Task StartDuelAsync(string leftModelId, string rightModelId, string prompt)
    {
        if (_connection is null) return;
        await _connection.InvokeAsync("StartDuel", leftModelId, rightModelId, prompt);
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
