// GoF: Observer — server pushes state changes to subscribed clients
using Microsoft.AspNetCore.SignalR;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Hubs;

public sealed class DuelHub : Hub
{
    /// <summary>Client calls this to join a duel's broadcast group.</summary>
    public async Task JoinDuel(string duelId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"duel:{duelId}");
    }

    /// <summary>Server calls this to push per-model status updates to the duel group.</summary>
    public async Task SendModelStatusUpdate(string duelId, ModelStatusUpdateDto update)
    {
        await Clients.Group($"duel:{duelId}").SendAsync("ModelStatusUpdate", update);
    }

    /// <summary>Server calls this to signal duel completion to the duel group.</summary>
    public async Task SendDuelComplete(string duelId, DuelDto duelDto)
    {
        await Clients.Group($"duel:{duelId}").SendAsync("DuelComplete", duelDto);
    }
}
