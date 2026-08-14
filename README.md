# PoLocalCompare — LLM Duel Arena

> **Live:** [polocalcompare.azurewebsites.net](https://polocalcompare.azurewebsites.net) · Full docs in [docs/PRD_Master.md](docs/PRD_Master.md) · Agent context in [AGENT.MD](AGENT.MD) · Generated reports in [docs/](docs/)

**What.** PoLocalCompare is a real-time LLM benchmarking arena. Two models race to generate HTML from the same prompt: **Local** models run entirely in your browser via WebLLM/WebGPU, **Remote** models call Azure AI Foundry, and (locally) **Ollama** models run as a service. Both outputs stream live over SignalR with token velocity, GPU placement, and energy telemetry. You judge the winner in side-by-side sandboxed viewports — or, if you don't pick within `AiJudge:DelaySeconds` (60 by default), an AI judge decides which output followed the prompt more accurately and records the verdict itself (configurable under `AiJudge`; verdicts are stored with the source that produced them). If exactly one model failed to produce output, the survivor takes a walkover without a judge call; if both failed, the duel stays pending and no rating moves. An Elo system (K=32, start 1200) ranks every model, with per-duel history, head-to-head "kill lists," Green Score (tokens/Wh) energy metrics, and exportable self-contained HTML lab reports.

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

## Tests

`dotnet test` per project under `tests/`. **Unit, Integration and E2E-API gate the deploy** — [.github/workflows/deploy.yml](.github/workflows/deploy.yml) runs all three in a `test` job that `build` depends on. Integration and E2E-API need Docker (Testcontainers spins Azurite).

**E2E-UI is deliberately not in CI**: it drives a real headed Chrome against a running instance and exercises WebGPU paths a runner has no GPU for. It is the one suite that goes stale silently — run it locally after touching UI markup.

## Documentation

| File | What it covers |
|---|---|
| [docs/PRD_Master.md](docs/PRD_Master.md) | Source of truth — slice boundaries, endpoint map, Table Storage schema, decision log (§9) |
| [AGENT.MD](AGENT.MD) | Living architectural contract — tech stack, structure, config keys, deployment, testing |
| [docs/ARCHITECTURE_REPORT.html](docs/ARCHITECTURE_REPORT.html) | C4 L1–L3 audit, `@page` → `MapGroup` slice boundaries, middleware ordering |
| [docs/AI_SERVICES_REPORT.html](docs/AI_SERVICES_REPORT.html) | Providers, model/deployment matrix, parameters, fallback policy, projected cost, telemetry gaps |
| [docs/ROLES_PERMISSIONS_MATRIX.html](docs/ROLES_PERMISSIONS_MATRIX.html) | Principal × environment access grid and the security audit behind it |
| [docs/USER_WORKFLOW.html](docs/USER_WORKFLOW.html) | End-to-end request trace and every failure mode |
| [docs/VISUAL_ARCHITECTURE_DASHBOARD.html](docs/VISUAL_ARCHITECTURE_DASHBOARD.html) | One-page visual map: components, sequences, ERD, narrative |

Each report carries three density tiers (30-second summary → overview → full implementation detail). Diagram sources are `docs/diagrams/*.mmd`; regenerate the rendered SVGs with:

```powershell
npx @mermaid-js/mermaid-cli -i docs/diagrams/<name>.mmd -o docs/assets/<name>.svg -b transparent
```
