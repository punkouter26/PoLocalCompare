using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Models;

public sealed class ListModelsHandler
{
    private readonly IModelRepository _modelRepository;

    public ListModelsHandler(IModelRepository modelRepository)
    {
        _modelRepository = modelRepository;
    }

    public async Task<IEnumerable<ModelDto>> HandleAsync()
    {
        var models = await _modelRepository.GetAllAsync();
        return models.Select(RegisterModelHandler.MapToDto);
    }
}
