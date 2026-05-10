// SOLID: Dependency Inversion
using PoLocalCompare.Domain.Entities;

namespace PoLocalCompare.Application.Interfaces;

public interface IModelRepository
{
    Task<Model?> GetByIdAsync(string modelId);
    Task<IEnumerable<Model>> GetAllAsync();
    Task SaveAsync(Model model);
    Task UpdateAsync(Model model);
}
