using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoLocalCompare.Client.Services;

public sealed partial class DuelApiClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DuelApiClient(HttpClient http)
    {
        _http = http;
    }
}
