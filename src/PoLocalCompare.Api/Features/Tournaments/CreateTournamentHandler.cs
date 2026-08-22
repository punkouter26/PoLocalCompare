using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Api.Features.Tournaments;

/// <summary>Draws a bracket from a chosen field and persists it, ready for the runner.</summary>
public sealed class CreateTournamentHandler(
    IModelRepository modelRepository,
    ITournamentRepository tournamentRepository)
{
    /// <summary>
    /// Model types a bracket may contain.
    /// </summary>
    /// <remarks>
    /// Server-side execution only, for the same reason <see cref="Demo.DemoPlanner"/> restricts
    /// its pool: a browser model runs WebGPU inference inside a foreground tab, and a bracket is
    /// designed to keep going after that tab is closed. Including one would produce a run that
    /// stalls at the first browser match with no way to finish it.
    /// </remarks>
    public static bool IsEligible(ModelType modelType) =>
        modelType is ModelType.Remote or ModelType.LocalService;

    /// <summary>
    /// The models that can enter a bracket, strongest first — which is also the seeding order,
    /// so the setup form can show the draw before the user commits to it.
    /// </summary>
    public async Task<IReadOnlyList<TournamentEntrantDto>> ListEntrantsAsync()
    {
        var models = await modelRepository.GetAllAsync();

        return models
            .Where(m => IsEligible(m.ModelType))
            .OrderByDescending(m => m.CurrentElo)
            .ThenBy(m => m.DisplayName, StringComparer.Ordinal)
            .Select(m => new TournamentEntrantDto
            {
                ModelId = m.ModelId,
                DisplayName = m.DisplayName,
                ModelType = m.ModelType,
                CurrentElo = Math.Round(m.CurrentElo, 1),
            })
            .ToList();
    }

    /// <summary>
    /// Draws and saves the bracket. Throws <see cref="ArgumentException"/> for a field that
    /// cannot make a valid bracket — the endpoint maps that to a 400.
    /// </summary>
    public async Task<TournamentDto> HandleAsync(
        IReadOnlyList<ModelId> modelIds,
        string promptText,
        string? actor)
    {
        var size = modelIds.Count;

        if (!BracketPlanner.IsSupportedSize(size))
        {
            throw new ArgumentException(
                $"A bracket needs {string.Join(", ", BracketPlanner.SupportedSizes)} models — got {size}.",
                nameof(modelIds));
        }

        if (modelIds.Distinct().Count() != size)
            throw new ArgumentException("A model cannot enter the same bracket twice.", nameof(modelIds));

        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("A tournament needs a prompt.", nameof(promptText));

        if (promptText.Length < CommenceDuelCommand.MinPromptLength)
        {
            throw new ArgumentException(
                $"The prompt must be at least {CommenceDuelCommand.MinPromptLength} characters.",
                nameof(promptText));
        }

        var catalog = (await modelRepository.GetAllAsync()).ToDictionary(m => m.ModelId);

        var entrants = new List<Model>(size);
        foreach (var id in modelIds)
        {
            if (!catalog.TryGetValue(id, out var model))
                throw new ArgumentException($"Model '{id}' is not in the catalog.", nameof(modelIds));

            if (!IsEligible(model.ModelType))
            {
                throw new ArgumentException(
                    $"{model.DisplayName} runs in the browser, so it cannot enter an unattended bracket.",
                    nameof(modelIds));
            }

            entrants.Add(model);
        }

        // Seeding is by current rating, strongest first — BracketPlanner stamps the seed numbers
        // and places them so the top two can only meet in the final.
        var seededField = entrants
            .OrderByDescending(m => m.CurrentElo)
            .ThenBy(m => m.DisplayName, StringComparer.Ordinal)
            .Select(m => new BracketSlot(m.ModelId, m.DisplayName))
            .ToList();

        var tournament = Tournament.Draw(
            TournamentId.New(),
            size,
            seededField,
            promptText.Trim(),
            actor);

        await tournamentRepository.SaveAsync(tournament);

        return tournament.ToDto();
    }
}
