// GoF: Repository pattern
using Azure.Data.Tables;
using Azure.Storage.Blobs;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class DuelResultRepository : IDuelResultRepository
{
    private const string TableName = "DuelResults";
    private const string BlobContainerName = "duel-html-outputs";
    private const long MaxTablePropertyBytes = 64 * 1024; // 64KB

    private readonly TableClient _tableClient;
    private readonly BlobContainerClient _blobContainerClient;

    public DuelResultRepository(TableServiceClient tableServiceClient, BlobServiceClient blobServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
        _blobContainerClient = blobServiceClient.GetBlobContainerClient(BlobContainerName);
    }

    public async Task SaveAsync(DuelResult result)
    {
        var htmlOutput = result.HtmlOutputRaw;
        var htmlBytes = System.Text.Encoding.UTF8.GetByteCount(htmlOutput);

        // If output exceeds 64KB, store in Blob Storage
        if (htmlBytes > MaxTablePropertyBytes)
        {
            await _blobContainerClient.CreateIfNotExistsAsync();
            var blobPath = $"{result.DuelId}/{result.ModelId}.html";
            var blobClient = _blobContainerClient.GetBlobClient(blobPath);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(htmlOutput));
            await blobClient.UploadAsync(stream, overwrite: true);
            htmlOutput = $"blob://{blobClient.Uri}";
        }

        var entity = MapToEntity(result, htmlOutput);
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    public async Task<DuelResult?> GetAsync(DuelId duelId, ModelId modelId)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(duelId, modelId);
            return await MapToResultAsync(response.Value);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IEnumerable<DuelResult>> GetByDuelIdAsync(DuelId duelId)
    {
        var results = new List<DuelResult>();
        // CreateQueryFilter escapes the value instead of pasting it into the OData expression.
        // Ids are not shape-validated (DuelId.From accepts any non-blank string so legacy rows
        // keep round-tripping), so a route-supplied id reaches here verbatim and a raw
        // interpolation would let `x' or PartitionKey ne 'x` widen the query to every partition.
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {duelId.Value}")))
        {
            results.Add(await MapToResultAsync(entity));
        }
        return results;
    }

    public async Task<IEnumerable<DuelResult>> GetByModelIdAsync(ModelId modelId)
    {
        var results = new List<DuelResult>();
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"RowKey eq {modelId.Value}")))
        {
            results.Add(await MapToResultAsync(entity));
        }
        return results;
    }

    private static TableEntity MapToEntity(DuelResult result, string htmlOutput)
    {
        return new TableEntity(result.DuelId, result.ModelId)
        {
            ["WarmUpDurationMs"] = result.WarmUpDurationMs,
            ["GenerationDurationMs"] = result.GenerationDurationMs,
            ["TotalDurationMs"] = result.TotalDurationMs,
            ["TokenCount"] = result.TokenCount,
            ["TokenVelocity"] = result.TokenVelocity,
            ["HtmlOutputRaw"] = htmlOutput,
            ["HtmlOutputSizeBytes"] = result.HtmlOutputSizeBytes,
            ["CharacterDensityRatio"] = result.CharacterDensityRatio,
            ["OutputQualityScore"] = result.OutputQualityScore,
            ["IsFailure"] = result.IsFailure,
            ["FailureReason"] = result.FailureReason,
            ["EnergyWh"] = result.EnergyWh,
            ["EnergyCostUsd"] = result.EnergyCostUsd,
            ["ApiCostUsd"] = result.ApiCostUsd,
            ["GreenScore"] = result.GreenScore
        };
    }

    private async Task<DuelResult> MapToResultAsync(TableEntity entity)
    {
        var htmlOutput = entity.GetString("HtmlOutputRaw") ?? string.Empty;

        // Resolve blob reference if needed
        if (htmlOutput.StartsWith("blob://", StringComparison.OrdinalIgnoreCase))
        {
            var blobUri = new Uri(htmlOutput["blob://".Length..]);
            var blobClient = new BlobClient(blobUri);
            var response = await blobClient.DownloadContentAsync();
            htmlOutput = response.Value.Content.ToString();
        }

        return new DuelResult
        {
            DuelId = DuelId.FromOrDefault(entity.PartitionKey),
            ModelId = ModelId.FromOrDefault(entity.RowKey),
            WarmUpDurationMs = entity.GetInt64("WarmUpDurationMs") ?? 0,
            GenerationDurationMs = entity.GetInt64("GenerationDurationMs") ?? 0,
            TotalDurationMs = entity.GetInt64("TotalDurationMs") ?? 0,
            TokenCount = entity.GetInt32("TokenCount") ?? 0,
            TokenVelocity = entity.GetDouble("TokenVelocity") ?? 0,
            HtmlOutputRaw = htmlOutput,
            HtmlOutputSizeBytes = entity.GetInt64("HtmlOutputSizeBytes") ?? 0,
            CharacterDensityRatio = entity.GetDouble("CharacterDensityRatio") ?? 0,
            OutputQualityScore = entity.GetInt32("OutputQualityScore") ?? HtmlOutputQualityScorer.Score(htmlOutput),
            IsFailure = entity.GetBoolean("IsFailure") ?? false,
            FailureReason = entity.GetString("FailureReason"),
            EnergyWh = entity.GetDouble("EnergyWh"),
            EnergyCostUsd = entity.GetDouble("EnergyCostUsd"),
            ApiCostUsd = entity.GetDouble("ApiCostUsd"),
            GreenScore = entity.GetDouble("GreenScore")
        };
    }
}
