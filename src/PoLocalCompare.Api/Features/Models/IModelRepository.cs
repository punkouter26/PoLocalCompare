// SOLID: Dependency Inversion

namespace PoLocalCompare.Api.Features.Models;

public interface IModelRepository
{
    Task<Model?> GetByIdAsync(string modelId);
    Task<IEnumerable<Model>> GetAllAsync();
    Task SaveAsync(Model model);
    Task UpdateAsync(Model model);
    Task DeleteAsync(string modelId);
}
