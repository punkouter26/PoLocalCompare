# PoLocalCompare — LLM Duel Arena

> **Live:** [polocalcompare.azurewebsites.net](https://polocalcompare.azurewebsites.net) · Full docs in [docs/PRD_Master.md](docs/PRD_Master.md) · Agent context in [AGENT.MD](AGENT.MD)

**What.** PoLocalCompare is a real-time LLM benchmarking arena. Two models race to generate HTML from the same prompt: **Local** models run entirely in your browser via WebLLM/WebGPU, **Remote** models call Azure AI Foundry, and (locally) **Ollama** models run as a service. Both outputs stream live over SignalR with token velocity, GPU placement, and energy telemetry. You judge the winner in side-by-side sandboxed viewports — or, if you don't pick within a few seconds, an AI judge decides which output followed the prompt more accurately and records the verdict itself (configurable under `AiJudge`; verdicts are stored with the source that produced them). An Elo system (K=32, start 1200) ranks every model, with per-duel history, head-to-head "kill lists," Green Score (tokens/Wh) energy metrics, and exportable self-contained HTML lab reports.

**Who.** A solo operator and invited guests, signing in with any Microsoft account through a BFF cookie session (the WASM client never touches tokens). The UI is mobile-portrait-first with an OLED dark theme.

**Why.** Cloud models cost money per token; browser models cost only watts. This app answers, with measured evidence rather than vibes, whether a free local model is actually competitive — in speed, output quality, and cost — with a paid cloud model for practical HTML-generation tasks.

## Local Setup (bare metal, Windows)

One command from the repo root — installs prerequisites via Winget, starts Docker/Azurite, configures local mock keys, and frees ports 5000/5001:

```powershell
pwsh SCRIPTS/setup.ps1
```

Then run the app (serves the Blazor WASM client at https://localhost:5001):

```powershell
dotnet run --project src/PoLocalCompare.Api --launch-profile https
```

Optional extras:

```powershell
python SCRIPTS/download-models.py                 # pre-download WebLLM browser model assets
dotnet user-secrets set "AzureAiFoundry:ApiKey" "<key>" --project src/PoLocalCompare.Api   # enable remote duels
```

Tests (never run in CI by policy): `dotnet test` per project under `tests/`. Architecture, endpoint map, schema, and flow diagrams live in [docs/](docs/).
