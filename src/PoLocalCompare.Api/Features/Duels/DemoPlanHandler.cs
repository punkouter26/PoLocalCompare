using PoLocalCompare.Shared.Demo;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// Resolves the schedule for an unattended demo run.
/// </summary>
/// <remarks>
/// Remote models only. Browser (WebGPU) models run inference in the client's tab, so an
/// unattended demo would stall on any machine without a GPU or with the tab backgrounded; Ollama
/// models are seeded in Development only and are not present in a deployed environment. Neither
/// is a safe default for a "press play and walk away" mode, so the pool is narrowed here rather
/// than failing round six.
/// </remarks>
public sealed class DemoPlanHandler(IModelRepository modelRepository)
{
    public const int MaxRounds = 25;

    public async Task<DemoPlanDto> HandleAsync(int rounds, int? seed = null)
    {
        var clampedRounds = Math.Clamp(rounds <= 0 ? DemoPlanner.DefaultRounds : rounds, 1, MaxRounds);

        var remoteModels = (await modelRepository.GetAllAsync())
            .Where(model => model.ModelType == ModelType.Remote)
            .OrderBy(model => model.ModelId)
            .ToList();

        if (remoteModels.Count < DemoPlanner.MinimumModels)
        {
            return new DemoPlanDto
            {
                AvailableModels = remoteModels.Count,
                UnavailableReason = remoteModels.Count == 0
                    ? "No remote models are registered. Demo mode runs server-side only, so it needs at least two Azure AI Foundry deployments."
                    : "Only one remote model is registered. Demo mode needs at least two to pair them.",
            };
        }

        var namesById = remoteModels.ToDictionary(model => model.ModelId, model => model.DisplayName);
        var plan = DemoPlanner.Plan(remoteModels.Select(model => model.ModelId).ToList(), clampedRounds, seed);

        return new DemoPlanDto
        {
            AvailableModels = remoteModels.Count,
            Rounds = plan.Select(round => new DemoRoundDto
            {
                Index = round.Index,
                PromptId = round.Prompt.Id,
                PromptTitle = round.Prompt.Title,
                PromptEmoji = round.Prompt.Emoji,
                PromptText = round.Prompt.Text,
                LeftModelId = round.LeftModelId,
                LeftModelName = namesById.GetValueOrDefault(round.LeftModelId, round.LeftModelId),
                RightModelId = round.RightModelId,
                RightModelName = namesById.GetValueOrDefault(round.RightModelId, round.RightModelId),
            }).ToList(),
        };
    }
}
