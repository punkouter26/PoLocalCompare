// SOLID: Single Responsibility
using NUlid;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Shared.DTOs;
using SharedModelType = PoLocalCompare.Shared.Enums.ModelType;
using DomainModelType = PoLocalCompare.Domain.Enums.ModelType;

namespace PoLocalCompare.Application.Models.RegisterModel;

public sealed class RegisterModelHandler
{
    private readonly IModelRepository _modelRepository;

    public RegisterModelHandler(IModelRepository modelRepository)
    {
        _modelRepository = modelRepository;
    }

    public async Task<ModelDto> HandleAsync(RegisterModelCommand command)
    {
        var modelId = Ulid.NewUlid().ToString();

        var domainModelType = command.ModelType == SharedModelType.Local
            ? DomainModelType.Local
            : DomainModelType.Remote;

        var model = new Model(
            modelId,
            command.DisplayName,
            domainModelType,
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
        ModelType = model.ModelType == DomainModelType.Local
            ? SharedModelType.Local
            : SharedModelType.Remote,
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
