// SOLID: Single Responsibility — lab report export coordinates data loading + rendering only

namespace PoLocalCompare.Api.Features.Archive;

public sealed class ExportLabReportHandler
{
    private readonly IDuelRepository _duelRepository;
    private readonly IDuelResultRepository _duelResultRepository;
    private readonly IEloHistoryRepository _eloHistoryRepository;
    private readonly ILabReportRenderer _renderer;

    public ExportLabReportHandler(
        IDuelRepository duelRepository,
        IDuelResultRepository duelResultRepository,
        IEloHistoryRepository eloHistoryRepository,
        ILabReportRenderer renderer)
    {
        _duelRepository = duelRepository;
        _duelResultRepository = duelResultRepository;
        _eloHistoryRepository = eloHistoryRepository;
        _renderer = renderer;
    }

    public async Task<string?> HandleAsync(ExportLabReportCommand command)
    {
        var duel = await _duelRepository.GetByIdAsync(command.DuelId);
        if (duel is null) return null;

        var results = await _duelResultRepository.GetByDuelIdAsync(command.DuelId);

        var leftElo = await _eloHistoryRepository.GetLast20Async(duel.LeftModelId);
        var rightElo = await _eloHistoryRepository.GetLast20Async(duel.RightModelId);
        var eloHistory = leftElo.Concat(rightElo);

        return await _renderer.RenderAsync(duel, results, eloHistory);
    }
}
