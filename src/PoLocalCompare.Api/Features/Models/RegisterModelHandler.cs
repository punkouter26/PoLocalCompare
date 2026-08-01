// SOLID: Single Responsibility
using NUlid;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

public sealed class RegisterModelHandler
{
    private readonly IModelRepository _modelRepository;

    public RegisterModelHandler(IModelRepository modelRepository)
    {
        _modelRepository = modelRepository;
    }

    public async Task<ModelDto> HandleAsync(RegisterModelCommand command)
    {
        // Prevent duplicate registrations by DisplayName
        var existing = await _modelRepository.GetAllAsync();
        if (existing.Any(m => string.Equals(m.DisplayName, command.DisplayName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A model named '{command.DisplayName}' is already registered.");

        var modelId = ModelId.New();

        var model = new Model(
            modelId,
            command.DisplayName,
            command.ModelType,
            command.TdpWatts,
            command.WebLlmModelId,
            command.ApiEndpointRef,
            command.InputTokenPricePerMillion,
            command.OutputTokenPricePerMillion);

        await _modelRepository.SaveAsync(model);

        return MapToDto(model);
    }

    internal static ModelDto MapToDto(Model model) => new()
    {
        ModelId = model.ModelId,
        DisplayName = model.DisplayName,
        ModelType = model.ModelType,
        CurrentElo = model.CurrentElo,
        DuelCount = model.DuelCount,
        WinCount = model.WinCount,
        GreenScoreAvg = model.GreenScoreAvg,
        TdpWatts = model.TdpWatts,
        ApiEndpointRef = model.ApiEndpointRef,
        WebLlmModelId = model.WebLlmModelId,
        InputTokenPricePerMillion = model.InputTokenPricePerMillion,
        OutputTokenPricePerMillion = model.OutputTokenPricePerMillion,
        CreatedAt = model.CreatedAt,
    };
}
