using System.Text.Json.Serialization;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Shared.Presentation;

public enum DiagStatus { Idle, Checking, Loading, Generating, Complete, Error, Cancelled }

public sealed class ModelDiagState
{
    public ModelDto Model { get; init; } = null!;
    public bool? IsDownloaded { get; set; }
    public bool Downloading { get; set; }   // true while server-side download is running
    public string AssetSource { get; set; } = "";
    public DiagStatus Status { get; set; } = DiagStatus.Idle;
    public int LoadProgress { get; set; }
    public string StepDetail { get; set; } = "";
    public int LoadMs { get; set; }
    public int FirstTokenMs { get; set; }
    public int TokensPerSec { get; set; }
    public int TotalTokens { get; set; }
    public string Output { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public bool ShowOutput { get; set; }
    public bool WarmCache { get; set; }

    [JsonIgnore]
    public TaskCompletionSource<bool> _completion { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ResetCompletion() =>
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOllamaModel => Model.ModelType == PoLocalCompare.Shared.Enums.ModelType.LocalService;
    public bool IsRemoteModel => Model.ModelType == PoLocalCompare.Shared.Enums.ModelType.Remote;
    public bool UsesCdnAssets => AssetSource.StartsWith("cdn", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (int Mb, string Label)> _vramTable = new()
    {
        // SmolLM2-135M is f32-quantised (q0f32), so weights alone are ~540 MB; the published
        // MLC artifact adds tokenizer + config overhead and lands near 580 MB on disk, which is
        // about the same as the q4f32 360M variant. The 720 MB figure that used to be here was
        // a copy-paste from Llama 3.2 1B and silently overstated the load on small GPUs.
        ["SmolLM2-135M-Instruct-q0f32-MLC"]          = (580,  "580 MB"),
        ["SmolLM2-360M-Instruct-q4f32_1-MLC"]         = (580,  "580 MB"),
        ["SmolLM2-1.7B-Instruct-q4f16_1-MLC"]         = (1774, "1.8 GB"),
        ["Qwen2.5-0.5B-Instruct-q4f32_1-MLC"]         = (1060, "1.1 GB"),
        ["Qwen2.5-1.5B-Instruct-q4f32_1-MLC"]         = (1888, "1.9 GB"),
        ["Qwen2.5-7B-Instruct-q4f16_1-MLC"]           = (5107, "5.1 GB"),
        ["Llama-3.2-1B-Instruct-q4f16_1-MLC"]         = (879,  "879 MB"),
        ["Llama-3.2-3B-Instruct-q4f16_1-MLC"]         = (2264, "2.3 GB"),
        ["Llama-3.1-8B-Instruct-q4f16_1-MLC"]         = (5001, "5.0 GB"),
        ["Phi-3.5-mini-instruct-q4f32_1-MLC"]         = (5483, "5.5 GB"),
        ["Phi-4-mini-instruct-q4f16_1-MLC"]           = (3437, "3.4 GB"),
        ["Mistral-7B-Instruct-v0.3-q4f16_1-MLC"]      = (4573, "4.6 GB"),
        ["gemma-2-2b-it-q4f16_1-MLC"]                 = (1895, "1.9 GB"),
        ["gemma3-1b-it-q4f16_1-MLC"]                  = (711,  "711 MB"),
        ["Qwen3-1.7B-q4f16_1-MLC"]                    = (2037, "2.0 GB"),
        ["DeepSeek-R1-Distill-Llama-8B-q4f16_1-MLC"]  = (5001, "5.0 GB"),
    };

    public string VramLabel => _vramTable.TryGetValue(Model.WebLlmModelId ?? "", out var v) ? v.Label : "";
    public int VramMb       => _vramTable.TryGetValue(Model.WebLlmModelId ?? "", out var v) ? v.Mb   : 0;
    public string VramTier  => VramMb switch { 0 => "", <= 800 => "ok", <= 1500 => "caution", _ => "warn" };
}
