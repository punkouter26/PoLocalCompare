using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PoLocalCompare.Api.Features.Models;

public static class ModelsEndpoints
{
    public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/models").WithTags("Models").RequireAuthorization();

        group.MapGet("/", async (
            [FromServices] ListModelsHandler handler,
            [FromServices] IWebHostEnvironment env) =>
        {
            var models = VisibleModels(await handler.HandleAsync(), env);

            return Results.Ok(models);
        })
        .WithName("ListModels")
        .WithSummary("Returns all registered models with current ELO and duel counts.")
        .Produces<IEnumerable<ModelDto>>();

        group.MapGet("/availability", async (
            [FromServices] ListModelsHandler handler,
            [FromServices] IWebHostEnvironment env,
            [FromServices] IConfiguration config,
            [FromServices] IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var modelList = VisibleModels(await handler.HandleAsync(), env);

            // Probe Ollama tags once for all LocalService models.
            string[] ollamaAvailableModels = [];
            bool ollamaChecked = false;
            string? ollamaError = null;
            if (modelList.Any(m => m.ModelType == ModelType.LocalService))
            {
                var ollamaBaseUrl = (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
                var ollamaStatusClient = httpClientFactory.CreateClient("OllamaStatus");
                try
                {
                    var tags = await ollamaStatusClient.GetFromJsonAsync<OllamaTagsResponse>($"{ollamaBaseUrl}/api/tags", cancellationToken);
                    ollamaAvailableModels = tags?.Models?.Select(m => m.Name).ToArray() ?? [];
                    ollamaChecked = true;
                }
                catch (Exception ex)
                {
                    ollamaError = $"Ollama unavailable: {ex.Message}";
                }
            }

            var foundryEndpoint = config["AzureAiFoundry:Endpoint"]?.TrimEnd('/');
            var foundryApiKey = config["AzureAiFoundry:ApiKey"];
            var foundryClient = httpClientFactory.CreateClient("Foundry");

            static async Task<(HttpStatusCode StatusCode, string Body)> SendProbeAsync(
                HttpClient client,
                string url,
                string apiKey,
                string body,
                CancellationToken ct)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("api-key", apiKey);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var resp = await client.SendAsync(req, cts.Token);
                var respBody = await resp.Content.ReadAsStringAsync(cts.Token);
                return (resp.StatusCode, respBody);
            }

            async Task<ModelAvailabilityDto> CheckAvailabilityAsync(ModelDto model)
            {
                if (model.ModelType == ModelType.Local)
                {
                    // Browser models may be downloaded on first run, so keep them selectable.
                    return new ModelAvailabilityDto
                    {
                        ModelId = model.ModelId,
                        IsAvailable = true,
                        Reason = null
                    };
                }

                if (model.ModelType == ModelType.LocalService)
                {
                    var modelRef = model.ApiEndpointRef ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(modelRef))
                    {
                        return new ModelAvailabilityDto
                        {
                            ModelId = model.ModelId,
                            IsAvailable = false,
                            Reason = "ApiEndpointRef is empty."
                        };
                    }

                    if (!ollamaChecked)
                    {
                        return new ModelAvailabilityDto
                        {
                            ModelId = model.ModelId,
                            IsAvailable = false,
                            Reason = ollamaError ?? "Unable to verify Ollama availability."
                        };
                    }

                    var available = ollamaAvailableModels.Any(m =>
                        m.Equals(modelRef, StringComparison.OrdinalIgnoreCase) ||
                        m.StartsWith(modelRef + ":", StringComparison.OrdinalIgnoreCase));

                    return new ModelAvailabilityDto
                    {
                        ModelId = model.ModelId,
                        IsAvailable = available,
                        Reason = available ? null : $"Not found in Ollama: {modelRef}"
                    };
                }

                // Remote model check against deployment endpoint first, then model inference endpoint.
                if (string.IsNullOrWhiteSpace(foundryEndpoint) || string.IsNullOrWhiteSpace(foundryApiKey))
                {
                    return new ModelAvailabilityDto
                    {
                        ModelId = model.ModelId,
                        IsAvailable = false,
                        Reason = "AzureAiFoundry endpoint or API key is missing."
                    };
                }

                if (string.IsNullOrWhiteSpace(model.ApiEndpointRef))
                {
                    return new ModelAvailabilityDto
                    {
                        ModelId = model.ModelId,
                        IsAvailable = false,
                        Reason = "ApiEndpointRef is empty."
                    };
                }

                var deploymentName = model.ApiEndpointRef;

                var deploymentUrl = FoundryChatRequest.DeploymentUrl(foundryEndpoint, deploymentName);
                var modelInferenceUrl = FoundryChatRequest.ModelInferenceUrl(foundryEndpoint);

                var probeMessages = new[] { new { role = "user", content = "Say OK." } };

                // Reasoning models (gpt-5*, o-series) reject max_tokens/temperature with HTTP 400.
                // Reasoning tokens count against the budget, so probe with enough headroom to return a token.
                var deploymentBody = JsonSerializer.Serialize(
                    FoundryChatRequest.Build(deploymentName, probeMessages, maxTokens: 16, temperature: 0, stream: false, includeModelField: false));

                var inferenceBody = JsonSerializer.Serialize(
                    FoundryChatRequest.Build(deploymentName, probeMessages, maxTokens: 16, temperature: 0, stream: false, includeModelField: true));

                try
                {
                    var (deploymentStatus, deploymentRespBody) = await SendProbeAsync(
                        foundryClient,
                        deploymentUrl,
                        foundryApiKey,
                        deploymentBody,
                        cancellationToken);

                    if ((int)deploymentStatus is >= 200 and < 300)
                    {
                        return new ModelAvailabilityDto
                        {
                            ModelId = model.ModelId,
                            IsAvailable = true,
                            Reason = null
                        };
                    }

                    if (deploymentStatus == HttpStatusCode.NotFound)
                    {
                        var (inferenceStatus, inferenceRespBody) = await SendProbeAsync(
                            foundryClient,
                            modelInferenceUrl,
                            foundryApiKey,
                            inferenceBody,
                            cancellationToken);

                        var fallbackAvailable = (int)inferenceStatus is >= 200 and < 300;
                        return new ModelAvailabilityDto
                        {
                            ModelId = model.ModelId,
                            IsAvailable = fallbackAvailable,
                            Reason = fallbackAvailable
                                ? null
                                : inferenceStatus == HttpStatusCode.NotFound
                                    ? "Model/deployment not found in this Azure AI Foundry resource."
                                    : $"Model endpoint unavailable (HTTP {(int)inferenceStatus})."
                        };
                    }

                    return new ModelAvailabilityDto
                    {
                        ModelId = model.ModelId,
                        IsAvailable = false,
                        Reason = deploymentStatus switch
                        {
                            HttpStatusCode.Unauthorized => "Foundry API key is invalid.",
                            HttpStatusCode.Forbidden => "Foundry access forbidden for this key/resource.",
                            HttpStatusCode.TooManyRequests => "Rate limited while probing. Try again shortly.",
                            _ => $"Deployment endpoint unavailable (HTTP {(int)deploymentStatus})."
                        }
                    };
                }
                catch (OperationCanceledException)
                {
                    return new ModelAvailabilityDto
                    {
                        ModelId = model.ModelId,
                        IsAvailable = false,
                        Reason = "Probe timed out."
                    };
                }
                catch (Exception)
                {
                    return new ModelAvailabilityDto
                    {
                        ModelId = model.ModelId,
                        IsAvailable = false,
                        Reason = "Probe failed."
                    };
                }
            }

            var results = await Task.WhenAll(modelList.Select(CheckAvailabilityAsync));

            return Results.Ok(results);
        })
        .WithName("GetModelAvailability")
        .WithSummary("Returns per-model runtime availability so only confirmed working models can be selected.")
        .Produces<IEnumerable<ModelAvailabilityDto>>();

        group.MapPost("/", async (
            [FromBody] RegisterModelRequest request,
            [FromServices] RegisterModelHandler handler) =>
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["DisplayName"] = ["DisplayName is required."]
                });

            var command = new RegisterModelCommand(
                request.DisplayName,
                request.ModelType,
                request.TdpWatts,
                request.WebLlmModelId,
                request.ApiEndpointRef,
                request.InputTokenPricePerMillion,
                request.OutputTokenPricePerMillion);

            try
            {
                var dto = await handler.HandleAsync(command);
                return Results.Created($"/api/models/{dto.ModelId}", dto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Request"] = [ex.Message]
                });
            }
        })
        .WithName("RegisterModel")
        .WithSummary("Registers a new model in the Model Registry.")
        .Produces<ModelDto>(StatusCodes.Status201Created)
        .ProducesValidationProblem();

        group.MapGet("/download-status/{webLlmModelId}", (
            [FromRoute] string webLlmModelId,
            [FromServices] IWebHostEnvironment env) =>
        {
            if (string.IsNullOrWhiteSpace(webLlmModelId))
            {
                return Results.BadRequest(new { error = "webLlmModelId is required." });
            }

            if (webLlmModelId.Contains("..", StringComparison.Ordinal) ||
                webLlmModelId.Contains('/', StringComparison.Ordinal) ||
                webLlmModelId.Contains('\\', StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "Invalid model id." });
            }

            var relativePath = $"models/{webLlmModelId}/mlc-chat-config.json";
            var fileInfo = env.WebRootFileProvider.GetFileInfo(relativePath);
            var downloaded = fileInfo.Exists && fileInfo.Length > 0;
            return Results.Ok(new ModelDownloadStatusDto(webLlmModelId, downloaded));
        })
        .WithName("GetModelDownloadStatus")
        .WithSummary("Checks whether a local WebLLM model asset has been downloaded.")
        .Produces<ModelDownloadStatusDto>();

        group.MapPatch("/{modelId}", async (
            [FromRoute] ModelId modelId,
            [FromBody] PatchModelRequest request,
            [FromServices] IModelRepository repository) =>
        {
            var model = await repository.GetByIdAsync(modelId);
            if (model is null) return Results.NotFound();

            var updated = new Model
            {
                ModelId = model.ModelId,
                DisplayName = request.DisplayName ?? model.DisplayName,
                ModelType = model.ModelType,
                CurrentElo = model.CurrentElo,
                DuelCount = model.DuelCount,
                WinCount = model.WinCount,
                GreenScoreAvg = model.GreenScoreAvg,
                TdpWatts = model.TdpWatts,
                WebLlmModelId = model.WebLlmModelId,
                ApiEndpointRef = request.ApiEndpointRef ?? model.ApiEndpointRef,
                ETag = model.ETag,
                InputTokenPricePerMillion = model.InputTokenPricePerMillion,
                OutputTokenPricePerMillion = model.OutputTokenPricePerMillion,
                CreatedAt = model.CreatedAt
            };

            await repository.UpdateAsync(updated);

            return Results.Ok(RegisterModelHandler.MapToDto(updated));
        })
        .WithName("PatchModel")
        .WithSummary("Updates DisplayName and/or ApiEndpointRef for an existing model.")
        .Produces<PoLocalCompare.Shared.DTOs.ModelDto>()
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{modelId}", async (
            [FromRoute] ModelId modelId,
            [FromServices] IModelRepository repository) =>
        {
            var model = await repository.GetByIdAsync(modelId);
            if (model is null) return Results.NotFound();
            await repository.DeleteAsync(modelId);
            return Results.NoContent();
        })
        .WithName("DeleteModel")
        .WithSummary("Removes a model from the registry.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        // POST /api/models/{webLlmModelId}/download — triggers background HuggingFace download
        group.MapPost("/{webLlmModelId}/download", async (
            [FromRoute] string webLlmModelId,
            [FromServices] IModelRepository modelRepo,
            [FromServices] IWebHostEnvironment env,
            [FromServices] ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("ModelDownload");
            // Validate format — only alphanumeric, dots, hyphens (prevents path traversal)
            if (!System.Text.RegularExpressions.Regex.IsMatch(webLlmModelId, @"^[a-zA-Z0-9._-]+$"))
                return Results.BadRequest(new { error = "Invalid model ID format." });

            var models = await modelRepo.GetAllAsync();
            var model = models.FirstOrDefault(m => m.WebLlmModelId == webLlmModelId);
            if (model is null)
                return Results.NotFound(new { error = $"No registered model with WebLlmModelId '{webLlmModelId}'." });

            var scriptPath = Path.GetFullPath(
                Path.Combine(env.ContentRootPath, "..", "..", "tools", "maintenance", "download-models.py"));

            if (!File.Exists(scriptPath))
                return Results.Problem(
                    "download-models.py not found. Run from repo root.",
                    statusCode: StatusCodes.Status500InternalServerError);

            // Start download as a detached background process — returns immediately.
            // Wrapped so a launch failure or non-zero exit is logged rather than lost.
            _ = Task.Run(async () =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "python",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                    };
                    psi.ArgumentList.Add(scriptPath);
                    psi.ArgumentList.Add("--model");
                    psi.ArgumentList.Add(webLlmModelId);

                    using var process = System.Diagnostics.Process.Start(psi);
                    if (process is null)
                    {
                        logger.LogError("Failed to start python for model download {Model}", webLlmModelId);
                        return;
                    }

                    var stderr = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0)
                        logger.LogError("Model download for {Model} exited with code {Code}: {Error}", webLlmModelId, process.ExitCode, stderr);
                    else
                        logger.LogInformation("Model download for {Model} completed.", webLlmModelId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Model download for {Model} threw.", webLlmModelId);
                }
            });

            return Results.AcceptedAtRoute(null, new { webLlmModelId, status = "Downloading in background. Refresh status in a few minutes." });
        })
        .WithName("DownloadModel")
        .WithSummary("Triggers background download of a local WebLLM model from HuggingFace.")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Drops Ollama-backed models outside Development. They need a local Ollama daemon that does
    /// not exist in the cloud, so the hosted catalog would otherwise advertise dead entries —
    /// the same reason <see cref="ModelSeeder"/> only seeds them in Development.
    /// </summary>
    private static List<ModelDto> VisibleModels(IEnumerable<ModelDto> models, IWebHostEnvironment env) =>
        env.IsDevelopment()
            ? models.ToList()
            : models.Where(model => model.ModelType != ModelType.LocalService).ToList();
}

public sealed record RegisterModelRequest(
    string DisplayName,
    ModelType ModelType,
    double? TdpWatts,
    string? WebLlmModelId,
    string? ApiEndpointRef,
    decimal? InputTokenPricePerMillion,
    decimal? OutputTokenPricePerMillion);

public sealed record PatchModelRequest(
    string? DisplayName,
    string? ApiEndpointRef);

public sealed record ModelDownloadStatusDto(
    string WebLlmModelId,
    bool Downloaded);

file sealed record OllamaTagsResponse(
    IReadOnlyList<OllamaTagModel>? Models);

file sealed record OllamaTagModel(
    string Name);
