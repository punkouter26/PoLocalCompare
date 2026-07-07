// SOLID: Dependency Inversion

namespace PoLocalCompare.Api.Features.Archive;

/// <summary>
/// Renders a self-contained HTML Lab Report for a completed duel.
/// </summary>
public interface ILabReportRenderer
{
    Task<string> RenderAsync(Duel duel, IEnumerable<DuelResult> results, IEnumerable<EloRecord> eloHistory);
}
