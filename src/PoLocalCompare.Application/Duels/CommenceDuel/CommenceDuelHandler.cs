// SOLID: Open/Closed — new model types extend without modifying handler
using NUlid;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Application.Duels.CommenceDuel;

public sealed class CommenceDuelHandler(
    IModelRepository modelRepository,
    IDuelRepository duelRepository,
    int verdictDeadlineHours = CommenceDuelCommand.DefaultVerdictDeadlineHours)
{
    private const string CdnSuffix =
        "\n\nIMPORTANT: Use public CDN links (e.g., cdnjs.cloudflare.com, unpkg.com) for all external libraries. Do not reference npm packages, local paths, or unpublished modules.";

    public async Task<DuelDto> HandleAsync(CommenceDuelCommand command)
    {
        if (command.LeftModelId == command.RightModelId)
            throw new ArgumentException("LeftModelId and RightModelId must differ.");

        if (string.IsNullOrWhiteSpace(command.PromptText))
            throw new ArgumentException("PromptText cannot be empty.", nameof(command.PromptText));

        if (command.PromptText.Length < CommenceDuelCommand.MinPromptLength)
            throw new ArgumentException($"PromptText must be at least {CommenceDuelCommand.MinPromptLength} characters.", nameof(command.PromptText));

        if (command.PromptText.Length > CommenceDuelCommand.MaxPromptLength)
            throw new ArgumentException($"PromptText cannot exceed {CommenceDuelCommand.MaxPromptLength} characters.", nameof(command.PromptText));

        var leftModel = await modelRepository.GetByIdAsync(command.LeftModelId)
            ?? throw new KeyNotFoundException($"Model '{command.LeftModelId}' not found.");
        var rightModel = await modelRepository.GetByIdAsync(command.RightModelId)
            ?? throw new KeyNotFoundException($"Model '{command.RightModelId}' not found.");

        var duelId = Ulid.NewUlid().ToString();
        var promptFull = command.PromptText + CdnSuffix;

        var duel = new Duel(
            duelId,
            command.PromptText,
            promptFull,
            command.LeftModelId,
            command.RightModelId,
            verdictDeadlineHours);

        await duelRepository.SaveAsync(duel);

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
            IsPartial = false,
        };
    }
}