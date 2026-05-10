// SOLID: Open/Closed — new model types extend without modifying handler
using NUlid;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Application.Duels.CommenceDuel;

public sealed class CommenceDuelHandler
{
    private const string CdnSuffix =
        "\n\nIMPORTANT: Use public CDN links (e.g., cdnjs.cloudflare.com, unpkg.com) for all external libraries. Do not reference npm packages, local paths, or unpublished modules.";

    private readonly IModelRepository _modelRepository;
    private readonly IDuelRepository _duelRepository;

    public CommenceDuelHandler(IModelRepository modelRepository, IDuelRepository duelRepository)
    {
        _modelRepository = modelRepository;
        _duelRepository = duelRepository;
    }

    public async Task<DuelDto> HandleAsync(CommenceDuelCommand command)
    {
        if (command.LeftModelId == command.RightModelId)
            throw new ArgumentException("LeftModelId and RightModelId must differ.");

        var leftModel = await _modelRepository.GetByIdAsync(command.LeftModelId)
            ?? throw new KeyNotFoundException($"Model '{command.LeftModelId}' not found.");
        var rightModel = await _modelRepository.GetByIdAsync(command.RightModelId)
            ?? throw new KeyNotFoundException($"Model '{command.RightModelId}' not found.");

        var duelId = Ulid.NewUlid().ToString();
        var promptFull = command.PromptText + CdnSuffix;

        var duel = new Duel(
            duelId,
            command.PromptText,
            promptFull,
            command.LeftModelId,
            command.RightModelId);

        await _duelRepository.SaveAsync(duel);

        return new DuelDto
        {
            DuelId = duel.DuelId,
            PromptText = duel.PromptText,
            PromptFull = duel.PromptFull,
            LeftModelId = duel.LeftModelId,
            RightModelId = duel.RightModelId,
            StartedAt = duel.StartedAt,
            Verdict = DuelVerdict.Pending,
            TimeLimitSeconds = 300,
        };
    }
}
