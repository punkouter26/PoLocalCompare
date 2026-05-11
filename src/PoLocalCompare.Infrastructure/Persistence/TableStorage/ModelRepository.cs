// GoF: Repository pattern
using Azure;
using Azure.Data.Tables;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Infrastructure.Persistence.TableStorage;

public sealed class ModelRepository : IModelRepository
{
    private const string TableName = "Models";
    private const string PartitionKey = "model";

    private readonly TableClient _tableClient;

    public ModelRepository(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
    }

    public async Task<Model?> GetByIdAsync(string modelId)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(PartitionKey, modelId);
            return MapToModel(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IEnumerable<Model>> GetAllAsync()
    {
        var models = new List<Model>();
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(e => e.PartitionKey == PartitionKey))
        {
            models.Add(MapToModel(entity));
        }
        return models;
    }

    public async Task SaveAsync(Model model)
    {
        var entity = MapToEntity(model);
        await _tableClient.AddEntityAsync(entity);
    }

    public async Task UpdateAsync(Model model)
    {
        var entity = MapToEntity(model);
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    private static TableEntity MapToEntity(Model model)
    {
        var entity = new TableEntity(PartitionKey, model.ModelId)
        {
            ["DisplayName"] = model.DisplayName,
            ["ModelType"] = model.ModelType.ToString(),
            ["CurrentElo"] = model.CurrentElo,
            ["DuelCount"] = model.DuelCount,
            ["WinCount"] = model.WinCount,
            ["GreenScoreAvg"] = model.GreenScoreAvg,
            ["TdpWatts"] = model.TdpWatts,
            ["WebLlmModelId"] = model.WebLlmModelId,
            ["ApiEndpointRef"] = model.ApiEndpointRef,
            ["InputTokenPricePerMillion"] = model.InputTokenPricePerMillion.HasValue
                ? (double?)((double)model.InputTokenPricePerMillion.Value)
                : null,
            ["OutputTokenPricePerMillion"] = model.OutputTokenPricePerMillion.HasValue
                ? (double?)((double)model.OutputTokenPricePerMillion.Value)
                : null,
            ["CreatedAt"] = model.CreatedAt
        };
        return entity;
    }

    private static Model MapToModel(TableEntity entity)
    {
        var model = new Model
        {
            ModelId = entity.RowKey,
            DisplayName = entity.GetString("DisplayName") ?? string.Empty,
            ModelType = Enum.Parse<ModelType>(entity.GetString("ModelType") ?? "Local"),
            CurrentElo = entity.GetDouble("CurrentElo") ?? 1200,
            DuelCount = entity.GetInt32("DuelCount") ?? 0,
            WinCount = entity.GetInt32("WinCount") ?? 0,
            GreenScoreAvg = entity.GetDouble("GreenScoreAvg") ?? 0,
            TdpWatts = entity.GetDouble("TdpWatts"),
            WebLlmModelId = entity.GetString("WebLlmModelId"),
            ApiEndpointRef = entity.GetString("ApiEndpointRef"),
            InputTokenPricePerMillion = entity.GetDouble("InputTokenPricePerMillion").HasValue
                ? (decimal?)Convert.ToDecimal(entity.GetDouble("InputTokenPricePerMillion")!.Value)
                : null,
            OutputTokenPricePerMillion = entity.GetDouble("OutputTokenPricePerMillion").HasValue
                ? (decimal?)Convert.ToDecimal(entity.GetDouble("OutputTokenPricePerMillion")!.Value)
                : null,
            CreatedAt = entity.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue
        };
        return model;
    }
}
