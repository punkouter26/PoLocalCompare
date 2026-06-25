using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PoLocalCompare.Client.Services;

public sealed partial class DuelApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DuelApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DuelApiClient(HttpClient http, ILogger<DuelApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }
}
