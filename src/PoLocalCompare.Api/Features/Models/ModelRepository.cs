// GoF: Repository pattern
using Azure;
using Azure.Data.Tables;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

public sealed class ModelRepository : IModelRepository
{
    private const string TableName = "Models";
    private const string PartitionKey = "model";

    private readonly TableClient _tableClient;

    public ModelRepository(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
    }

    public async Task<Model?> GetByIdAsync(ModelId modelId)
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
        try
        {
            await _tableClient.AddEntityAsync(entity);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Idempotent create (standards §5.5): the entity already exists, e.g. a retried request.
        }
    }

    public async Task UpdateAsync(Model model)
    {
        var entity = MapToEntity(model);
        // ETag-conditional replace (standards §5.5): a concurrent writer surfaces as 412 instead of a lost update.
        await _tableClient.UpdateEntityAsync(entity, TableETag.Parse(model.ETag), TableUpdateMode.Replace);
    }

    public async Task DeleteAsync(ModelId modelId)
    {
        try
        {
            await _tableClient.DeleteEntityAsync(PartitionKey, modelId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Idempotent delete: already gone.
        }
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
            ["DrawCount"] = model.DrawCount,
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
            ModelId = ModelId.FromOrDefault(entity.RowKey),
            DisplayName = entity.GetString("DisplayName") ?? string.Empty,
            ModelType = Enum.Parse<ModelType>(entity.GetString("ModelType") ?? "Local"),
            CurrentElo = entity.GetDouble("CurrentElo") ?? 1200,
            DuelCount = entity.GetInt32("DuelCount") ?? 0,
            WinCount = entity.GetInt32("WinCount") ?? 0,
            // Absent on rows written before ties existed; 0 is the correct backfill.
            DrawCount = entity.GetInt32("DrawCount") ?? 0,
            TdpWatts = entity.GetDouble("TdpWatts"),
            WebLlmModelId = entity.GetString("WebLlmModelId"),
            ApiEndpointRef = entity.GetString("ApiEndpointRef"),
            InputTokenPricePerMillion = entity.GetDouble("InputTokenPricePerMillion").HasValue
                ? (decimal?)Convert.ToDecimal(entity.GetDouble("InputTokenPricePerMillion")!.Value)
                : null,
            OutputTokenPricePerMillion = entity.GetDouble("OutputTokenPricePerMillion").HasValue
                ? (decimal?)Convert.ToDecimal(entity.GetDouble("OutputTokenPricePerMillion")!.Value)
                : null,
            CreatedAt = entity.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
            ETag = entity.ETag.ToString()
        };
        return model;
    }
}
