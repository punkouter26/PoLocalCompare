using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

internal static class ModelVisibility
{
    /// <summary>
    /// Drops Ollama-backed models outside Development. They need a local Ollama daemon that does
    /// not exist in the cloud, so the hosted catalog would otherwise advertise dead entries —
    /// the same reason <see cref="ModelSeeder"/> only seeds them in Development.
    /// </summary>
    public static List<ModelDto> Filter(IEnumerable<ModelDto> models, IWebHostEnvironment environment) =>
        environment.IsDevelopment()
            ? models.ToList()
            : models.Where(model => model.ModelType != ModelType.LocalService).ToList();
}
