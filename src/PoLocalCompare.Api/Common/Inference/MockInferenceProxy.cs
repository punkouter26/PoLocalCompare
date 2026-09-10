using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Stand-in <see cref="IRemoteInferenceProxy"/> served when <c>Features:UseRealAi</c> is off.
/// </summary>
/// <remarks>
/// This exists to make the flag honest. Until 2026-09-10 nothing in the server read
/// <c>Features:UseRealAi</c> — only the navbar's "USING MOCK DATA" banner and the /diag config
/// dump did — so a run with the flag off still called Foundry for every duel while the UI told
/// the user the responses were simulated. Registering this proxy is what makes that banner true.
///
/// The output is deliberately fixed rather than random. Mock mode is for exercising the whole
/// pipeline (queue, streaming, persistence, verdict, ELO) without spending tokens or needing a
/// Foundry key, and a deterministic document makes a strange result reproducible. The model's
/// own name is echoed into the page so the two sides of a duel are still distinguishable.
///
/// Registered last so it wins over the real proxies for its key (MS.DI resolves the final
/// matching descriptor). Tests bypass it entirely by replacing the keyed registrations with
/// their own mocks, and it is never registered in Production — see
/// <c>InfrastructureServiceExtensions.AddInfrastructure</c>.
/// </remarks>
public sealed class MockInferenceProxy(ILogger<MockInferenceProxy> logger) : IRemoteInferenceProxy
{
    private const int ChunkCount = 8;
    private static readonly TimeSpan ChunkDelay = TimeSpan.FromMilliseconds(40);

    public async Task<DuelResult> RunInferenceAsync(
        Model model,
        DuelId duelId,
        string promptFull,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var html = BuildDocument(model, promptFull);
        var chunkSize = Math.Max(1, html.Length / ChunkCount);
        var stopwatch = Stopwatch.StartNew();
        var emitted = 0;

        // Stream in chunks rather than returning immediately: it keeps the Arena's token race,
        // the streaming preview and the RenderCoalescer on the same code path they take for a
        // real model, so mock mode actually exercises the UI instead of skipping past it.
        for (var offset = 0; offset < html.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(ChunkDelay, cancellationToken);

            emitted += Math.Min(chunkSize, html.Length - offset);
            await onTokenUpdate(
                emitted,
                stopwatch.ElapsedMilliseconds,
                new HtmlStreamStats(0, 0, 0, 0, offset == 0 ? html[..emitted] : null));
        }

        stopwatch.Stop();
        var elapsed = stopwatch.ElapsedMilliseconds;
        logger.LogInformation(
            "Mock inference served {ModelName} ({ModelId}) for duel {DuelId}.",
            model.DisplayName, model.ModelId, duelId);

        return new DuelResult(duelId, model.ModelId)
        {
            HtmlOutputRaw = html,
            // Characters streamed, not tokens. Mock mode has no tokeniser, and the field is only
            // ever displayed — nothing adjudicates on it while the flag is off.
            TokenCount = emitted,
            GenerationDurationMs = elapsed,
            TotalDurationMs = elapsed,
            HtmlOutputSizeBytes = Encoding.UTF8.GetByteCount(html),
            FinishReason = "stop",
            ApiCostUsd = 0,
        };
    }

    private static string BuildDocument(Model model, string promptFull)
    {
        var name = System.Net.WebUtility.HtmlEncode(model.DisplayName);
        var prompt = System.Net.WebUtility.HtmlEncode(
            promptFull.Length > 120 ? promptFull[..120] + "…" : promptFull);
        var elapsed = DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture);

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>Mock output — {{name}}</title>
              <style>
                html, body { margin: 0; height: 100%; }
                body {
                  display: grid; place-content: center; gap: 8px;
                  background: #10131a; color: #e6e9ef;
                  font: 14px/1.5 system-ui, sans-serif; text-align: center;
                }
                strong { font-size: 20px; }
                code { color: #9fb6d6; font-size: 11px; }
              </style>
            </head>
            <body>
              <strong>{{name}}</strong>
              <span>Mock response — Features:UseRealAi is off.</span>
              <code>{{prompt}}</code>
              <code>{{elapsed}} UTC</code>
            </body>
            </html>
            """;
    }
}
