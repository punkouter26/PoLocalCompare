using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Runs the browser half of a duel: a WebLLM model executes in this tab, and its finished output
/// is POSTed back so the server can settle the duel.
/// </summary>
/// <remarks>
/// This is the asymmetric half of the architecture — remote and Ollama models stream from the
/// server over the hub, but a WebGPU model never touches the server during generation, so the
/// client has to drive it and report the result. It lived inline in <c>Arena.razor</c>, which
/// made the page the only place that knew how, and made it unreachable from any test tier except
/// E2E-UI. The Arena now owns only the view state (which side is a browser model); everything
/// about actually running one is here.
///
/// Instantiated per Arena rather than registered in DI: <see cref="_running"/> is per-duel
/// bookkeeping, and a scoped service in a WebAssembly host is app-wide, so a second duel in the
/// same tab would inherit the first one's guard set.
/// </remarks>
public sealed class LocalInferenceDriver(WebLlmService webLlm, DuelApiClient api)
{
    /// <summary>
    /// Models already generating in this tab. A hub reconnect replays <c>StartLocalInference</c>,
    /// and starting a second worker for the same model would race two outputs into one result.
    /// </summary>
    private readonly HashSet<ModelId> _running = [];

    /// <summary>
    /// Claims the model for this tab. Returns false when the signal is a replay for a model
    /// already generating, in which case the caller must not start anything.
    /// </summary>
    public bool TryClaim(ModelId modelId) => _running.Add(modelId);

    /// <summary>
    /// Generates in the browser and posts the outcome. Never throws: a failure still has to
    /// reach the server, or the duel sits unfinished forever.
    /// </summary>
    public async Task RunAsync(StartLocalInferencePayload payload)
    {
        var htmlOutput = string.Empty;
        var tokenCount = 0;
        long totalMs = 0;
        long warmUpMs = 0;
        var isFailure = false;
        string? failureReason = null;

        try
        {
            // WebLlmService needs the prompt, which only the duel record has.
            var duel = await api.GetDuelAsync(payload.DuelId);
            var prompt = duel?.PromptFull ?? string.Empty;

            await foreach (var update in webLlm.StartInferenceAsync(
                payload.ModelId, payload.WebLlmModelId ?? payload.ModelId, prompt, payload.DuelId))
            {
                if (update.Status is "Done" or "Failed")
                {
                    isFailure = update.Status == "Failed";
                    failureReason = update.ErrorReason;
                    break;
                }
            }

            if (!isFailure)
            {
                var result = await webLlm.GetResultAsync(payload.ModelId);
                if (result is not null)
                {
                    htmlOutput = result.HtmlOutput;
                    tokenCount = result.TokenCount;
                    totalMs = result.TotalMs;
                    warmUpMs = result.WarmUpMs;
                }
            }
        }
        catch (Exception ex)
        {
            isFailure = true;
            failureReason = ex.Message;
        }
        finally
        {
            _running.Remove(payload.ModelId);
        }

        // Posted even on failure: the server needs the result to settle the duel either way.
        await api.PostLocalResultAsync(
            payload.DuelId, payload.ModelId, htmlOutput, tokenCount, totalMs, warmUpMs, isFailure, failureReason);
    }
}
