using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Tests.E2E.Api;

/// <summary>
/// Guards the documented API surface: the generated document must be OpenAPI 3.1
/// (standards §3) and the Scalar reference UI must be reachable in development.
/// </summary>
[Collection("E2EAPI")]
public sealed class OpenApiContractTests(ApiAppFixture app)
{
    [Fact]
    public async Task OpenApiDocument_IsVersion31()
    {
        using var client = app.CreateAnonymousClient();
        var doc = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        var version = doc.GetProperty("openapi").GetString();
        Assert.StartsWith("3.1", version);
    }

    [Fact]
    public async Task ScalarReference_IsServed()
    {
        using var client = app.CreateAnonymousClient();
        var response = await client.GetAsync("/scalar");

        // Scalar may canonicalise /scalar → /scalar/{document}; follow one hop.
        if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            response = await client.GetAsync(response.Headers.Location);
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
