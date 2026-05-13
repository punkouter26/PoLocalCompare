using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Application.Models.RegisterModel;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Application.Models.ListModels;

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
