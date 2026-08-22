// SOLID: Open/Closed — new model types extend without modifying handler
using Microsoft.Extensions.Options;
using NUlid;
using PoLocalCompare.Api.Auth;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class CommenceDuelHandler(
    IModelRepository modelRepository,
    IDuelRepository duelRepository,
    IOptions<AutoJudgeOptions> autoJudgeOptions,
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

        var duelId = DuelId.New();
        var promptFull = command.PromptText + CdnSuffix;

        var duel = new Duel(
            duelId,
            command.PromptText,
            promptFull,
            command.LeftModelId,
            command.RightModelId,
            verdictDeadlineHours)
        {
            // Snapshotted at creation so the duel stays readable if either model is later
            // retired from the catalog — see Duel.LeftModelName.
            LeftModelName = leftModel.DisplayName,
            RightModelName = rightModel.DisplayName,
            // Forensic only — never used for authorization. "anonymous" is the sentinel the
            // endpoint injects when the open gate is in effect and no claim is present.
            OwnerId = command.Actor ?? IdentityResolver.AnonymousActor,
            // Stamped at creation: the rule a duel is judged by has to be part of its record,
            // not a parameter of the run that adjudicated it.
            ChallengeKind = command.ChallengeKind,
            ChallengeThreshold = command.ChallengeThreshold,
        };

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
            AutoJudgeDelaySeconds = autoJudgeOptions.Value.Enabled
                ? command.AutoJudgeDelaySecondsOverride ?? autoJudgeOptions.Value.DelaySeconds
                : 0,
            IsPartial = false,
            ChallengeKind = duel.ChallengeKind,
            ChallengeThreshold = duel.ChallengeThreshold,
            LeftModelType = leftModel.ModelType,
            RightModelType = rightModel.ModelType,
            OwnerId = duel.OwnerId,
            VerdictBy = duel.VerdictBy,
        };
    }
}