# Implementation Plan: PoLocalCompare — LLM Duel Arena

**Branch**: `001-llm-duel-arena` | **Date**: 2026-05-09 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/001-llm-duel-arena/spec.md`

## Summary

PoLocalCompare is a benchmarking platform that pits local browser-based LLMs (running via WebLLM/WebGPU in a Web Worker) against remote cloud models (proxied through the .NET backend to Azure AI Foundry) in timed HTML-generation duels. A standard Elo rating system (K=32) tracks model performance over time. Results are stored in Azure Table Storage and surfaced across four pages: War Room (configuration), Arena (judging), Leaderboard (ELO rankings + Green Score), and Lab Archive (history + HTML Lab Report export).

The server is built with **Onion Architecture** (.NET 10, C# 14): Domain → Application → Infrastructure → Api. The client is a simple **Blazor WASM** app (Radzen components, OLED Black theme) hosted within the Api project. Real-time processing-phase updates flow via **SignalR**. All secrets live in **Azure Key Vault** accessed via Managed Identity. Local dev uses **Azurite in Docker**.

## Technical Context

**Language/Version**: C# 14 / .NET 10 (pinned via `global.json`)
**Primary Dependencies**: ASP.NET Core (server), Blazor WASM (client), Radzen (UI), WebLLM (in-browser inference via WebGPU), `Azure.AI.Inference` (Foundry proxy), `Azure.Data.Tables` (storage), SignalR (real-time), Serilog + OpenTelemetry (observability), Testcontainers + Playwright (testing)
**Storage**: Azure Table Storage — 4 tables (`Models`, `Duels`, `DuelResults`, `EloHistory`); Azurite in Docker for local dev
**Testing**: xUnit (unit + integration with Testcontainers/Azurite), Playwright/TypeScript (E2E, headed in Dev); AI calls mocked in Integration + E2E (`Features:UseRealAi = false`)
**Target Platform**: Azure App Services (`PoShared` App Service Plan) + Blazor WASM hosted in Api (HTTP 5000 / HTTPS 5001 locally)
**Project Type**: Client/Server web application — Onion Architecture server + simple Blazor WASM client
**Performance Goals**: Arena viewports visible ≤ 1 second after both models finish; ELO visible on Leaderboard ≤ 3 seconds after verdict; SignalR HUD updates every 500ms during duel
**Constraints**: HTTP 5000 / HTTPS 5001; no secrets in appsettings; TreatWarningsAsErrors; Nullable enabled; WebGPU-capable browser required for local models; 5-minute watchdog per model
**Scale/Scope**: Single-user benchmarking tool; personal Azure subscription `Punkouter26`; small model registry (tens of models)

## Constitution Check

*GATE: Re-evaluated post-Phase 1 design — all items PASS.*

- [x] **I. Naming**: Solution `PoLocalCompare.sln`; all projects carry `PoLocalCompare.*` prefix; `global.json` pins .NET 10.
- [x] **II. Architecture**: Onion Architecture confirmed (Domain / Application / Infrastructure / Api); client is simple Blazor WASM with Radzen; SOLID/GoF pattern comments required at implementation (e.g., Repository pattern in Infrastructure, Strategy pattern for ELO variants, Observer via SignalR).
- [x] **III. Structure**: `Directory.Packages.props` + `Directory.Build.props` at root; `PoLocalCompare.Shared` planned for DTOs; `wwwroot` in client only; `src/` + `tests/` layout confirmed.
- [x] **IV. API Standards**: Ports 5000/5001; Scalar/OpenAPI enabled; `.http` request files planned for all endpoints; `/diag` (Blazor page) and `/health` (JSON endpoint) both in scope (FR-031, FR-032).
- [x] **V. Azure/Secrets**: No secrets in appsettings; Azure Key Vault via Managed Identity; App Service Plan references `PoShared` RG; Table Storage in `PoLocalCompare-rg`.
- [ ] **VI. Auth/Security**: ⚠ **CONSTITUTION CONFLICT — amendment required.** Constitution §VI mandates "Microsoft OAuth MUST be supported in both development and production environments" (unconditional MUST, not scoped to "if auth is used"). Spec Assumption explicitly opts out of authentication for this single-user tool. These are irreconcilable without a constitution change. **Action**: run `/speckit.constitution` to add an explicit exemption clause (e.g., "Single-user personal tools with no multi-user data isolation requirement are exempt from the OAuth MUST") then re-mark this gate `[x]`. OWASP Top 10 portion passes independently: model HTML output sanitised (XSS), iframe `sandbox="allow-scripts"` without `allow-same-origin`, API keys never reach client.
- [x] **VII. Testing**: Unit (Domain ELO logic, Application services); Integration (Testcontainers Azurite, API endpoints); E2E Playwright headed; AI feature flag `Features:UseRealAi`; `MOCK DATA` banner wired into FRs.
- [x] **VIII. Observability**: Serilog (File + Console + App Insights sinks); OpenTelemetry to `PoShared` App Insights; log context includes `UserId` (anonymous for this feature), `SessionId`, `CorrelationId`, `Environment`, full exceptions; dev-mode stack traces in UI.
- [x] **IX. Hygiene**: Feature flag for AI integration (`Features:UseRealAi`); ELO K-factor configurable via appsettings; `/LLMDOCS` to be created alongside implementation; ambiguity stop-rule applied (5 clarification questions asked and answered).
- [x] **X. DX**: F5 task kills existing dotnet processes and opens Edge; Scalar + `.http` files planned; `/diag` page covers all connection types including WebGPU client-side status.

**Complexity Tracking** — one justified deviation:

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| Client-side JS interop (WebLLM Web Worker) | Local model inference requires WebGPU, which is only accessible from JavaScript in the browser | Pure C# WASM inference is not yet supported by WebGPU APIs; WebLLM is the only production-ready in-browser LLM runtime as of .NET 10 era |

## Project Structure

### Documentation (this feature)

```text
specs/001-llm-duel-arena/
├── plan.md              # This file
├── research.md          # Phase 0 — all unknowns resolved
├── data-model.md        # Phase 1 — entities, tables, validation rules
├── quickstart.md        # Phase 1 — developer onboarding & validation checklist
├── contracts/
│   └── api.md           # Phase 1 — REST endpoints + SignalR hub contract
└── tasks.md             # Phase 2 — generated by /speckit.tasks (not yet created)
```

### Source Code

```text
src/
├── PoLocalCompare.Domain/
│   ├── Entities/
│   │   ├── Model.cs               # // GoF: Entity; domain model for LLM entry
│   │   ├── Duel.cs                # // GoF: Entity; Aggregate root for a benchmarking session
│   │   ├── DuelResult.cs          # // GoF: Value Object (per-model telemetry snapshot)
│   │   └── EloRecord.cs           # // GoF: Entity; immutable ELO snapshot
│   ├── Services/
│   │   └── EloCalculator.cs       # // SOLID: Single Responsibility; pure ELO formula
│   ├── ValueObjects/
│   │   ├── GreenScore.cs          # tokens / Wh
│   │   └── CharacterDensity.cs
│   └── Enums/
│       ├── ModelType.cs           # Local | Remote
│       ├── DuelVerdict.cs         # Left | Right | Pending
│       └── DuelStatus.cs          # Initializing | Generating | Done | Failed
│
├── PoLocalCompare.Application/
│   ├── Duels/
│   │   ├── CommenceDuel/          # Use case: POST /api/duels
│   │   ├── RecordVerdict/         # Use case: POST /api/duels/{id}/verdict
│   │   ├── GetDuel/               # Use case: GET /api/duels/{id}
│   │   └── ListDuels/             # Use case: GET /api/duels
│   ├── Models/
│   │   ├── RegisterModel/
│   │   └── ListModels/
│   ├── Leaderboard/
│   │   ├── GetLeaderboard/
│   │   └── GetKillList/
│   ├── Archive/
│   │   └── ExportLabReport/
│   ├── Interfaces/
│   │   ├── IDuelRepository.cs     # // SOLID: Dependency Inversion
│   │   ├── IModelRepository.cs
│   │   ├── IEloHistoryRepository.cs
│   │   ├── IRemoteInferenceProxy.cs
│   │   └── ILabReportRenderer.cs
│   └── DTOs/                      # Shared via PoLocalCompare.Shared
│
├── PoLocalCompare.Infrastructure/
│   ├── Persistence/
│   │   ├── TableStorage/
│   │   │   ├── DuelRepository.cs      # // GoF: Repository pattern
│   │   │   ├── ModelRepository.cs     # // GoF: Repository pattern
│   │   │   └── EloHistoryRepository.cs
│   │   └── AzuriteSetup.cs            # Dev-only table initialisation
│   ├── AzureAiFoundry/
│   │   └── FoundryInferenceProxy.cs   # // GoF: Proxy pattern; SOLID: Interface Segregation
│   ├── TdpEngine/
│   │   └── GreenStatsCalculator.cs    # Energy/cost calculation
│   ├── Reporting/
│   │   └── RazorLabReportRenderer.cs  # // GoF: Template Method (Razor view)
│   └── KeyVault/
│       └── KeyVaultExtensions.cs
│
├── PoLocalCompare.Api/
│   ├── Endpoints/                     # Minimal API route groups
│   │   ├── ModelsEndpoints.cs
│   │   ├── DuelsEndpoints.cs
│   │   ├── LeaderboardEndpoints.cs
│   │   └── HealthEndpoints.cs
│   ├── Hubs/
│   │   └── DuelHub.cs                 # SignalR hub for processing-phase events
│   ├── Pages/
│   │   └── Diag.razor                 # /diag diagnostics page
│   ├── Reports/                       # Razor views for Lab Report rendering
│   │   └── LabReport.cshtml
│   ├── requests/                      # .http files for all endpoints
│   │   ├── models.http
│   │   ├── duels.http
│   │   └── leaderboard.http
│   ├── appsettings.json
│   ├── appsettings.Development.json   # .gitignored
│   └── Program.cs
│
└── PoLocalCompare.Shared/
    ├── DTOs/
    │   ├── ModelDto.cs
    │   ├── DuelDto.cs
    │   ├── DuelResultDto.cs
    │   └── LeaderboardEntryDto.cs
    └── Enums/                         # Shared enums safe for WASM
        └── ...

src/Client/
└── PoLocalCompare.Client/
    ├── wwwroot/
    │   ├── index.html
    │   ├── css/
    │   │   └── app.css              # OLED Black theme (#000000)
    │   ├── js/
    │   │   ├── webllm-worker.js     # Web Worker: WebLLM inference + postMessage telemetry
    │   │   └── audio.js             # Web Audio API: snare-roll + success cue
    │   └── audio/
    │       ├── snare-roll.wav
    │       └── success.wav
    ├── Pages/
    │   ├── WarRoom.razor            # Page 1: model selection + prompt + commence
    │   ├── Processing.razor         # In-progress HUD (status labels, token counts)
    │   ├── Arena.razor              # Page 2: dual viewports + verdict
    │   ├── Leaderboard.razor        # Page 3: ELO table + sparklines + Kill List
    │   └── Archive.razor            # Page 4: Lab Archive + export
    ├── Components/
    │   ├── ModelCard.razor          # War Room model registry entry (ELO + projected shift)
    │   ├── ProcessingPanel.razor    # Per-model status panel during duel
    │   ├── TelemetryHud.razor       # Arena HUD overlay
    │   ├── SandboxedViewport.razor  # <iframe srcdoc> isolation wrapper
    │   ├── EloSparkline.razor       # SVG sparkline for leaderboard rows
    │   └── MockDataBanner.razor     # MOCK DATA banner (shown when UseRealAi=false)
    ├── Services/
    │   ├── WebLlmService.cs         # JS interop wrapper for webllm-worker.js
    │   ├── DuelApiClient.cs         # HTTP client for /api/duels
    │   ├── SignalRDuelClient.cs     # SignalR connection for /hubs/duel
    │   └── AudioService.cs          # Web Audio API interop
    └── Program.cs

tests/
├── unit/
│   └── PoLocalCompare.Unit.Tests/
│       ├── Domain/
│       │   └── EloCalculatorTests.cs
│       └── Application/
│           ├── CommenceDuelTests.cs
│           └── RecordVerdictTests.cs
├── integration/
│   └── PoLocalCompare.Integration.Tests/
│       ├── DuelsEndpointTests.cs    # Testcontainers Azurite
│       ├── LeaderboardTests.cs
│       └── MockAiFactory.cs         # WebApplicationFactory with UseRealAi=false
└── e2e/
    └── PoLocalCompare.E2E/
        ├── war-room.spec.ts
        ├── arena.spec.ts
        ├── leaderboard.spec.ts
        └── archive.spec.ts

/LLMDOCS/
├── README.md                # Quick codebase orientation for LLMs
├── architecture.md          # Onion layer boundaries + dependency rules
├── data-model.md            # Link to specs data-model.md
└── api-surface.md           # Link to specs contracts/api.md

global.json                  # Pins .NET 10 SDK
Directory.Build.props        # TreatWarningsAsErrors=true; Nullable=enable
Directory.Packages.props     # Central Package Management (all NuGet versions here)
.gitignore
PoLocalCompare.sln
```

**Structure Decision**: Onion Architecture server (5 projects: Domain, Application, Infrastructure, Api, Shared) + simple Blazor WASM client (1 project). No VSA-style Vertical Slice separation — VSA feature-folder organisation is applied *within* the Application layer's use case subfolders as a compatible internal convention.
