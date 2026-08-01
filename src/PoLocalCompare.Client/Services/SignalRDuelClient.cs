using Microsoft.AspNetCore.SignalR.Client;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

/// <summary>Connects to /hubs/duel and exposes typed SignalR events for duel status updates.</summary>
public sealed class SignalRDuelClient : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<ModelStatusUpdateDto>? OnModelStatusUpdate;
    public event Action<DuelDto>? OnDuelComplete;
    public event Action<StartLocalInferencePayload>? OnStartLocalInference;

    /// <summary>Raised when the auto-judge decides a duel nobody picked in time.</summary>
    public event Action<VerdictResponseDto>? OnVerdictRecorded;

    public async Task ConnectAsync(string baseUrl, DuelId duelId, CancellationToken cancellationToken = default)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/duel")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<ModelStatusUpdateDto>("ModelStatusUpdate", update =>
            OnModelStatusUpdate?.Invoke(update));

        _connection.On<DuelDto>("DuelComplete", dto =>
            OnDuelComplete?.Invoke(dto));

        _connection.On<StartLocalInferencePayload>("StartLocalInference", payload =>
            OnStartLocalInference?.Invoke(payload));

        _connection.On<VerdictResponseDto>("VerdictRecorded", dto =>
            OnVerdictRecorded?.Invoke(dto));

        await _connection.StartAsync(cancellationToken);
        await _connection.InvokeAsync("JoinDuel", duelId, cancellationToken);
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

public sealed record StartLocalInferencePayload(DuelId DuelId, ModelId ModelId, string Side, string? WebLlmModelId = null);
