// GoF: Observer — server pushes state changes to subscribed clients
using Microsoft.AspNetCore.SignalR;

namespace PoLocalCompare.Api.Features.Duels;

/// <remarks>
/// Server→client pushes go through <c>IHubContext&lt;DuelHub&gt;</c> (see
/// <see cref="DuelExecutionService"/>); the hub itself exposes only the client-invokable
/// join. Broadcast methods must not live here — being hub methods would let any
/// authenticated client push arbitrary status into any duel's group.
/// </remarks>
public sealed class DuelHub : Hub
{
    /// <summary>The app-wide activity group, as opposed to a single duel's group.</summary>
    public const string LobbyGroup = "lobby";

    /// <summary>Client calls this to join a duel's broadcast group.</summary>
    public async Task JoinDuel(DuelId duelId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"duel:{duelId}");
    }

    /// <summary>
    /// Joins the global activity feed. Carries no arguments and grants no ability to push —
    /// like <see cref="JoinDuel"/>, this only subscribes; broadcasts come from
    /// <see cref="LobbyNotifier"/> via <c>IHubContext</c>.
    /// </summary>
    public async Task JoinLobby()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup);
    }
}
