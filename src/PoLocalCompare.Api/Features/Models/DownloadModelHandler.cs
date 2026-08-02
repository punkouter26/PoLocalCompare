using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Kicks off a background vendoring run of a browser model's weights via SCRIPTS/download-models.py.
/// Fire-and-forget by design: a full model is gigabytes, so the request returns 202 and the health
/// panel re-probes for the assets afterwards.
/// </summary>
public sealed partial class DownloadModelHandler(
    IModelRepository modelRepository,
    IWebHostEnvironment environment,
    ILogger<DownloadModelHandler> logger)
{
    public enum Outcome { Accepted, InvalidId, UnknownModel, ScriptMissing }

    /// <summary>Only alphanumerics, dots, hyphens and underscores — this value reaches a process argument.</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial Regex SafeModelIdRegex();

    public async Task<Outcome> HandleAsync(string webLlmModelId)
    {
        if (!SafeModelIdRegex().IsMatch(webLlmModelId))
            return Outcome.InvalidId;

        var models = await modelRepository.GetAllAsync();
        if (!models.Any(m => m.WebLlmModelId == webLlmModelId))
            return Outcome.UnknownModel;

        var scriptPath = ResolveScriptPath();
        if (scriptPath is null)
            return Outcome.ScriptMissing;

        // Detached background process — returns immediately. Wrapped so a launch failure or
        // non-zero exit is logged rather than lost.
        _ = Task.Run(() => RunAsync(scriptPath, webLlmModelId));

        return Outcome.Accepted;
    }

    /// <summary>
    /// The API runs from src/PoLocalCompare.Api, so the repo root is two levels up; probing both
    /// the content root and its ancestors keeps this working under `dotnet run` and a published
    /// layout alike.
    /// </summary>
    private string? ResolveScriptPath()
    {
        string[] candidates =
        [
            Path.Combine(environment.ContentRootPath, "..", "..", "SCRIPTS", "download-models.py"),
            Path.Combine(environment.ContentRootPath, "SCRIPTS", "download-models.py"),
        ];

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    private async Task RunAsync(string scriptPath, string webLlmModelId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(webLlmModelId);

            using var process = Process.Start(psi);
            if (process is null)
            {
                logger.LogError("Failed to start python for model download {Model}", webLlmModelId);
                return;
            }

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                logger.LogError("Model download for {Model} exited with code {Code}: {Error}",
                    webLlmModelId, process.ExitCode, stderr);
            else
                logger.LogInformation("Model download for {Model} completed.", webLlmModelId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Model download for {Model} threw.", webLlmModelId);
        }
    }
}
