// Resolves the acting user for forensic audit stamping on writes. The Duel entity
// records OwnerId (creator) and VerdictBy (judge) so the leaderboard can tell a guest's
// verdict from a real user's. For authenticated callers this is the preferred_username
// claim (set by the OIDC scheme and the Fake scheme). For un-authenticated callers it
// returns the constant "anonymous" — see Features:AllowAnonymousWrites in Program.cs.
using System.Security.Claims;

namespace PoLocalCompare.Api.Auth;

public static class IdentityResolver
{
    public const string AnonymousActor = "anonymous";

    /// <summary>
    /// Returns the actor responsible for the current request, or
    /// <see cref="AnonymousActor"/> if no <c>preferred_username</c> claim is present.
    /// Never returns null.
    /// </summary>
    public static string ResolveActor(ClaimsPrincipal? user)
    {
        var name = user?.FindFirst("preferred_username")?.Value;
        return string.IsNullOrWhiteSpace(name) ? AnonymousActor : name;
    }
}
