// SOLID: Single Responsibility — lab report export coordinates data loading + rendering only

namespace PoLocalCompare.Api.Features.Archive;

public sealed class ExportLabReportHandler(
    IDuelRepository duelRepository,
    IDuelResultRepository duelResultRepository,
    IEloHistoryRepository eloHistoryRepository)
{
    public async Task<string?> HandleAsync(DuelId duelId)
    {
        var duel = await duelRepository.GetByIdAsync(duelId);
        if (duel is null) return null;

        var results = await duelResultRepository.GetByDuelIdAsync(duelId);

        var leftElo = await eloHistoryRepository.GetLast20Async(duel.LeftModelId);
        var rightElo = await eloHistoryRepository.GetLast20Async(duel.RightModelId);
        var eloHistory = leftElo.Concat(rightElo);

        // Rendering is a pure string transform with one implementation and no test double,
        // so it is a static call rather than an injected ILabReportRenderer.
        return HtmlLabReportRenderer.Render(duel, results, eloHistory);
    }
}
