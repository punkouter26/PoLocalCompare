---
description: "Task list for PoLocalCompare — LLM Duel Arena"
---

# Tasks: PoLocalCompare — LLM Duel Arena

**Input**: Design documents from `specs/001-llm-duel-arena/`
**Prerequisites**: plan.md ✅ spec.md ✅ research.md ✅ data-model.md ✅ contracts/api.md ✅ quickstart.md ✅

**Tests**: Not included by default — add with `/speckit.tasks --tests` if TDD is required.

**Organization**: Tasks grouped by user story — each story is an independently deliverable MVP increment.

## Path Conventions (Constitution § III)

- **Domain**: `src/PoLocalCompare.Domain/`
- **Application**: `src/PoLocalCompare.Application/`
- **Infrastructure**: `src/PoLocalCompare.Infrastructure/`
- **API host**: `src/PoLocalCompare.Api/`
- **Shared DTOs**: `src/PoLocalCompare.Shared/`
- **Blazor WASM client**: `src/Client/PoLocalCompare.Client/`
- **Unit tests**: `tests/unit/PoLocalCompare.Unit.Tests/`
- **Integration tests**: `tests/integration/PoLocalCompare.Integration.Tests/`
- **E2E tests**: `tests/e2e/PoLocalCompare.E2E/`
- **LLM docs**: `LLMDOCS/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Repository scaffolding, build configuration, and project skeleton. Must complete before any user story work.

- [X] T001 Create solution file `PoLocalCompare.sln` at repo root with `dotnet new sln`
- [X] T002 Create `global.json` at repo root pinning latest stable .NET 10 SDK version
- [X] T003 Create `Directory.Build.props` at repo root with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<Nullable>enable</Nullable>`
- [X] T004 Create `Directory.Packages.props` at repo root with Central Package Management (`<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`); add initial package versions for all dependencies identified in plan.md; explicitly include `NUlid` (ULID generation for `Duels` RowKey in T033)
- [X] T005 Create `.gitignore` covering `.vs/`, `.vscode/`, `bin/`, `obj/`, `*.user`, `*.suo`, `appsettings.Development.json`, and all standard .NET artifacts
- [X] T006 Create project `src/PoLocalCompare.Domain/PoLocalCompare.Domain.csproj` (class library, net10.0, no external references)
- [X] T007 Create project `src/PoLocalCompare.Application/PoLocalCompare.Application.csproj` (class library, references Domain only)
- [X] T008 Create project `src/PoLocalCompare.Infrastructure/PoLocalCompare.Infrastructure.csproj` (class library, references Application; add `Azure.Data.Tables`, `Azure.AI.Inference`, `Azure.Identity`, `Azure.Security.KeyVault.Secrets`, `NUlid`; do NOT add `Microsoft.AspNetCore.SignalR.Client` — Infrastructure has no client-side SignalR dependency; server hub uses `IHubContext<DuelHub>` via the server SDK in PoLocalCompare.Api)
- [X] T009 Create project `src/PoLocalCompare.Shared/PoLocalCompare.Shared.csproj` (class library, net10.0; no server-only or client-only framework refs)
- [X] T010 Create project `src/PoLocalCompare.Api/PoLocalCompare.Api.csproj` (ASP.NET Core web app, references Infrastructure + Shared; add `Scalar.AspNetCore`, `Microsoft.AspNetCore.SignalR`, `Serilog.AspNetCore`, `Serilog.Sinks.ApplicationInsights`, `OpenTelemetry.Extensions.Hosting`)
- [X] T011 Create project `src/Client/PoLocalCompare.Client/PoLocalCompare.Client.csproj` (Blazor WASM, references Shared; add `Radzen.Blazor`, `Microsoft.AspNetCore.SignalR.Client`)
- [X] T012 Add all 5 server projects + client project to `PoLocalCompare.sln`
- [X] T013 Add test projects to solution: `tests/unit/PoLocalCompare.Unit.Tests/`, `tests/integration/PoLocalCompare.Integration.Tests/`, `tests/e2e/PoLocalCompare.E2E/` (Playwright TS — `package.json` + `playwright.config.ts`)
- [X] T014 Configure Blazor WASM hosting: reference `PoLocalCompare.Client` from `PoLocalCompare.Api` and add `app.MapFallbackToFile("index.html")` in `Program.cs`; delete `wwwroot/` from Api project if scaffolded by template
- [X] T015 Configure `src/PoLocalCompare.Api/appsettings.json` with all non-secret keys (`Elo:KFactor`, `Elo:StartingRating`, `GreenStats:DefaultTdpWatts`, `GreenStats:ElectricityRateUsd`, `Duel:TimeLimitSeconds`, `Features:UseRealAi`) and placeholder comments for Key Vault secrets
- [X] T016 Configure `launchSettings.json` in Api project to fix HTTP port 5000 and HTTPS port 5001
- [X] T017 Create `.vscode/tasks.json` F5 launch task: kill existing dotnet processes → `dotnet run` Api → open `https://localhost:5001` in Edge
- [X] T018 Configure Serilog in `Program.cs`: File + Console + App Insights sinks; enrich with `UserId`, `SessionId`, `CorrelationId`, `Environment` log context properties
- [X] T019 Configure OpenTelemetry in `Program.cs`: trace + metrics exported to App Insights (`PoShared` resource); add `OTEL_EXPORTER_OTLP_ENDPOINT` to appsettings
- [X] T020 [P] Add Scalar OpenAPI middleware in `Program.cs` (`app.MapScalarApiReference()`)
- [X] T021 [P] Create `LLMDOCS/README.md`, `LLMDOCS/architecture.md`, `LLMDOCS/api-surface.md` with initial codebase orientation content
- [X] T099 [P] Create `azure.yaml` at repo root for Azure Developer CLI (`azd`) — set `name: PoLocalCompare`; reference the `infra/` folder; define service `api` pointing to `src/PoLocalCompare.Api/`
- [X] T100 Create `infra/main.bicep` — provision Azure Table Storage account in `PoLocalCompare-rg` with 4 tables (`Models`, `Duels`, `DuelResults`, `EloHistory`) and Blob Storage container `duel-html-outputs` (private, no TTL); reference App Service Plan from `PoShared` RG using Bicep `existing` keyword (do not recreate); apply Managed Identity RBAC roles `Storage Table Data Contributor` and `Storage Blob Data Contributor` on the storage account for the App Service identity; resource naming convention `PoLocalCompare-{ResourceType}-{Environment}`

**Checkpoint**: `dotnet build` passes with zero warnings; `azd provision --dry-run` succeeds; solution structure matches plan.md tree.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that ALL user stories depend on — persistence, domain entities, shared DTOs, health/diag, and observability wiring. Must be complete before any user story phase.

- [X] T022 Create Domain enums: `src/PoLocalCompare.Domain/Enums/ModelType.cs` (`Local | Remote`), `DuelVerdict.cs` (`Left | Right | Pending`), `DuelStatus.cs` (`Initializing | Generating | Done | Failed`)
- [X] T023 Create Domain entity `src/PoLocalCompare.Domain/Entities/Model.cs` — fields per data-model.md; include `// GoF: Entity` comment; `CurrentElo` initialised to 1200 on construction; remote model variants MUST include `InputTokenPricePerMillion` and `OutputTokenPricePerMillion` (decimal, nullable for local models) to enable API cost calculation per FR-018
- [X] T024 Create Domain entity `src/PoLocalCompare.Domain/Entities/Duel.cs` — aggregate root; holds refs to both models, prompt fields, timestamps, Verdict; enforce `LeftModelId ≠ RightModelId` in constructor; include `// GoF: Aggregate Root` comment
- [X] T025 Create Domain entity `src/PoLocalCompare.Domain/Entities/DuelResult.cs` — all telemetry fields per data-model.md; include `// GoF: Entity` comment
- [X] T026 Create Domain entity `src/PoLocalCompare.Domain/Entities/EloRecord.cs` — immutable snapshot; no setters; include `// GoF: Entity (immutable)` comment
- [X] T027 Create Domain value objects: `src/PoLocalCompare.Domain/ValueObjects/GreenScore.cs` (tokens/Wh), `CharacterDensity.cs` (functional chars / total chars); include `// GoF: Value Object` comments
- [X] T028 Create Domain service `src/PoLocalCompare.Domain/Services/EloCalculator.cs` — pure static implementation of standard Elo formula `E_a = 1/(1+10^((Rb-Ra)/400))`, `R'_a = Ra + K*(Sa - Ea)`; configurable K via parameter; include `// SOLID: Single Responsibility — pure ELO formula only` comment; returns results to 1 decimal place
- [X] T029 Create Application interfaces: `src/PoLocalCompare.Application/Interfaces/IDuelRepository.cs`, `IModelRepository.cs`, `IEloHistoryRepository.cs`, `IRemoteInferenceProxy.cs`, `ILabReportRenderer.cs`; include `// SOLID: Dependency Inversion` comment on each; `IEloHistoryRepository` MUST define both `Task<IEnumerable<EloRecord>> GetLast20(string modelId)` (used by leaderboard sparklines) and `Task<IEnumerable<EloRecord>> GetAllByModel(string modelId)` (used by Kill List aggregation in T071 — without this method Kill List cannot be built)
- [X] T030 Create Shared DTOs in `src/PoLocalCompare.Shared/DTOs/`: `ModelDto.cs`, `DuelDto.cs`, `DuelSummaryDto.cs`, `DuelResultDto.cs`, `LeaderboardEntryDto.cs`, `VerdictRequestDto.cs`, `VerdictResponseDto.cs`, `ModelStatusUpdateDto.cs` (SignalR message shape per contracts/api.md)
- [X] T031 Create Shared enums in `src/PoLocalCompare.Shared/` mirroring Domain enums safe for WASM use: `ModelType.cs`, `DuelVerdict.cs`, `DuelStatus.cs`
- [X] T032 Implement Azure Table Storage repository `src/PoLocalCompare.Infrastructure/Persistence/TableStorage/ModelRepository.cs` — CRUD against `Models` table (PartitionKey: `"model"`, RowKey: `modelId`); include `// GoF: Repository pattern` comment; register in DI
- [X] T033 Implement Azure Table Storage repository `src/PoLocalCompare.Infrastructure/Persistence/TableStorage/DuelRepository.cs` — writes to `Duels` table (PartitionKey: `YYYYMM`, RowKey: ULID); append-only verdict update; include `// GoF: Repository pattern` comment; register in DI
- [X] T034 Implement Azure Table Storage repository `src/PoLocalCompare.Infrastructure/Persistence/TableStorage/EloHistoryRepository.cs` — append-only writes to `EloHistory` table; implement both `GetLast20(modelId)` (top-20 query using inverted-tick RowKey, for sparklines) and `GetAllByModel(modelId)` (full partition scan for a single modelId, for Kill List aggregation); include `// GoF: Repository pattern` comment; register in DI
- [X] T035 Implement `src/PoLocalCompare.Infrastructure/Persistence/TableStorage/DuelResultRepository.cs` — writes `DuelResults` table (PartitionKey: duelId, RowKey: modelId); when `HtmlOutputRaw` exceeds 64KB, upload to Blob Storage container `duel-html-outputs` at path `{duelId}/{modelId}.html` (private access, no TTL) and store the blob URI prefixed with `blob://` in the table field; register in DI
- [X] T036 Create dev table initialisation helper `src/PoLocalCompare.Infrastructure/Persistence/AzuriteSetup.cs` — creates all 4 tables if they don't exist; called only when `ASPNETCORE_ENVIRONMENT = Development`
- [X] T037 Wire Key Vault in `src/PoLocalCompare.Infrastructure/KeyVault/KeyVaultExtensions.cs`: `AddAzureKeyVault` using `DefaultAzureCredential`; called from `Program.cs`
- [X] T038 Implement `/health` endpoint in `src/PoLocalCompare.Api/Endpoints/HealthEndpoints.cs` — JSON response pinging Azure Table Storage, Azure AI Foundry, and Key Vault; return 200 Healthy / 503 Unhealthy per contracts/api.md
- [X] T039 Implement `/diag` Blazor page `src/PoLocalCompare.Api/Pages/Diag.razor` — display Table Storage status + latency, Foundry endpoint status, Key Vault status, config keys with masked sensitive values (first 4 + `****` + last 4), WebGPU availability (JS interop from client on load), feature flag states
- [X] T040 [P] Add CORS policy in `Program.cs` restricting origins to `localhost:5000` and `localhost:5001` only
- [X] T041 [P] Create `src/PoLocalCompare.Api/requests/models.http`, `duels.http`, `leaderboard.http` — stub files for all endpoints from contracts/api.md, populated with example request bodies
- [X] T098 Create `src/Client/PoLocalCompare.Client/Services/DuelApiClient.cs` — typed HTTP client base registered as scoped `HttpClient` in client `Program.cs`; implement all methods required by Phases 3–5: `CommenceDuelAsync`, `RecordVerdictAsync`, `GetDuelAsync`, `GetModelsAsync`; Archive-specific methods (`ListDuelsAsync`, `DownloadReportAsync`) extended in T082 (Phase 6); this task is in Phase 2 to unblock T051 which requires `DuelApiClient` to forward WebLLM results

**Checkpoint**: `dotnet build` passes; `/health` returns `{"status":"Healthy"}`; `/diag` page loads; Azure Table Storage connects (via Azurite); Scalar UI at `/scalar` lists all registered endpoints.

---

## Phase 3: User Story 1 — Configure and Launch a Duel (Priority: P1) 🎯 MVP

**Goal**: A user selects one local and one remote model from the War Room, enters a prompt, presses "Commence Duel," hears the snare-roll audio cue, and both models start running concurrently. The processing-phase HUD shows live per-model status, token count, and elapsed time.

**Independent Test**: Select models → enter prompt → press Commence → verify audio cue plays → verify processing HUD shows two panels with live status labels updating over SignalR.

### Implementation for User Story 1

- [X] T042 [P] [US1] Create Application use case `src/PoLocalCompare.Application/Models/RegisterModel/RegisterModelCommand.cs` + `RegisterModelHandler.cs` — validates display name, type, required fields per data-model.md; calls `IModelRepository.SaveAsync`; include `// SOLID: Single Responsibility` comment
- [X] T043 [P] [US1] Create Application use case `src/PoLocalCompare.Application/Models/ListModels/ListModelsQuery.cs` + `ListModelsHandler.cs` — returns all models mapped to `ModelDto`; projects ELO + duel counts
- [X] T044 [US1] Implement `GET /api/models` and `POST /api/models` in `src/PoLocalCompare.Api/Endpoints/ModelsEndpoints.cs` — wire to handlers; return shapes per contracts/api.md; annotate with OpenAPI metadata for Scalar
- [X] T045 [US1] Create Application use case `src/PoLocalCompare.Application/Duels/CommenceDuel/CommenceDuelCommand.cs` + `CommenceDuelHandler.cs` — validates `LeftModelId ≠ RightModelId`, both exist; appends CDN pragmatism suffix to prompt (literal text appended verbatim: `"\n\nIMPORTANT: Use public CDN links (e.g., cdnjs.cloudflare.com, unpkg.com) for all external libraries. Do not reference npm packages, local paths, or unpublished modules."`); creates Duel entity (Verdict=Pending); persists via `IDuelRepository`; returns `DuelDto` with `duelId`; include `// SOLID: Open/Closed — new model types extend without modifying handler` comment
- [X] T046 [US1] Implement `POST /api/duels` in `src/PoLocalCompare.Api/Endpoints/DuelsEndpoints.cs` — wire to `CommenceDuelHandler`; after persisting, enqueue background duel execution (see T047); return 202 per contracts/api.md
- [X] T047 [US1] Implement background duel execution service `src/PoLocalCompare.Api/Services/DuelExecutionService.cs` — receives duelId; retrieves both models; starts two concurrent `Task`s (one per model); each task: (a) fires `ModelStatusUpdate` "Initializing" via `DuelHub`, (b) calls inference (remote: `IRemoteInferenceProxy`; local: signals client Web Worker via SignalR), (c) fires "Generating" status updates every 500ms with token count and elapsed ms, (d) enforces `CancellationTokenSource(300 seconds)` watchdog; on completion/timeout persists `DuelResult`; when both tasks settle fires `DuelComplete` via hub; include `// GoF: Strategy — inference execution varies by model type` comment
- [X] T048 [US1] Implement SignalR hub `src/PoLocalCompare.Api/Hubs/DuelHub.cs` — `JoinDuel(duelId)` adds caller to group; `SendModelStatusUpdate` and `SendDuelComplete` server methods per contracts/api.md hub spec; include `// GoF: Observer — server pushes state changes to subscribed clients` comment
- [X] T049 [US1] Implement Azure AI Foundry proxy `src/PoLocalCompare.Infrastructure/AzureAiFoundry/FoundryInferenceProxy.cs` implementing `IRemoteInferenceProxy` — calls `Azure.AI.Inference` SDK with configured deployment; streams tokens internally to track count + velocity; enforces cancellation token; returns `DuelResult` fields; include `// GoF: Proxy pattern; SOLID: Interface Segregation` comment
- [X] T050 [P] [US1] Create `src/Client/PoLocalCompare.Client/wwwroot/js/webllm-worker.js` — Web Worker loading WebLLM via CDN; accepts `{modelId, prompt}` via `postMessage`; emits `{type:'status', status, tokenCount, elapsedMs}` messages every 500ms; emits `{type:'complete', htmlOutput, tokenCount, totalMs, warmUpMs}` on finish; emits `{type:'error', reason}` on failure/timeout
- [X] T051 [P] [US1] Create `src/Client/PoLocalCompare.Client/Services/WebLlmService.cs` — JS interop wrapper; starts `webllm-worker.js` via `IJSRuntime`; exposes `StartInference(modelId, prompt, CancellationToken)` returning async enumerable of status updates; forwards final result to server via `DuelApiClient` (created in T098, Phase 2 — must be completed before this task)
- [X] T052 [P] [US1] Create `src/Client/PoLocalCompare.Client/wwwroot/js/audio.js` + `wwwroot/audio/snare-roll.wav` + `wwwroot/audio/success.wav` — Web Audio API helpers; `playSnareRoll()` and `playSuccess()` functions; pre-load `AudioBuffer` on module init; graceful no-op if `AudioContext` unavailable
- [X] T053 [P] [US1] Create `src/Client/PoLocalCompare.Client/Services/AudioService.cs` — JS interop; exposes `PlaySnareRoll()` and `PlaySuccess()` methods; swallows `JSException` silently (audio unavailable path)
- [X] T054 [P] [US1] Create `src/Client/PoLocalCompare.Client/Services/SignalRDuelClient.cs` — connects to `/hubs/duel`; subscribes to `ModelStatusUpdate` and `DuelComplete`; exposes `IAsyncEnumerable<ModelStatusUpdateDto>` and `DuelCompleteEvent`
- [X] T055 [US1] Create `src/Client/PoLocalCompare.Client/Components/ModelCard.razor` — displays model name, type badge, current ELO, and projected ELO gain/loss against currently selected opponent; selection state managed via `@bind`; uses Radzen `RadzenCard`
- [X] T056 [US1] Create `src/Client/PoLocalCompare.Client/Pages/WarRoom.razor` — dual-column model registry (local left, remote right) using `ModelCard` components; large prompt `RadzenTextArea`; 5-minute timer constraint display; display guidance text "No local models available — download a WebLLM-compatible model first" when local model list is empty; "Commence Duel" `RadzenButton` (disabled until valid pair + non-empty prompt selected); on click: call `AudioService.PlaySnareRoll()`, `POST /api/duels`, connect SignalR hub, navigate to `Processing.razor`; read `?prompt=`, `?leftModelId=`, `?rightModelId=` query parameters in `OnInitializedAsync` to pre-fill prompt and model selection (supports Re-Challenge flow from T068); include `<MockDataBanner />` at top of page (FR-034)
- [X] T057 [US1] Create `src/Client/PoLocalCompare.Client/Components/ProcessingPanel.razor` — per-model status panel; displays model name, status label (`Initializing → Generating → Done / Failed`), elapsed time counter (client-side timer), live token count, estimated time remaining; updated on each `ModelStatusUpdate` SignalR message
- [X] T058 [US1] Create `src/Client/PoLocalCompare.Client/Pages/Processing.razor` — two `ProcessingPanel` components side-by-side (desktop CSS Grid) / stacked (mobile CSS scroll-snap); subscribes to SignalR hub for duelId; on `DuelComplete` event navigates to `Arena.razor`; OLED Black theme applied via scoped CSS; include `<MockDataBanner />` at top of page (FR-034)
- [X] T059 [US1] Create `src/Client/PoLocalCompare.Client/Components/MockDataBanner.razor` — reads `Features:UseRealAi` from injected config; renders a prominent red `MOCK DATA` banner at the top of the page when false

**Checkpoint**: War Room loads with model registry; selecting one local + one remote + non-empty prompt enables the button; clicking fires audio cue and transitions to Processing page; SignalR hub receives and broadcasts status updates every 500ms; `POST /api/duels` returns 202; watchdog terminates and marks failure at 300 seconds.

---

## Phase 4: User Story 2 — Judge Results in the Arena (Priority: P2)

**Goal**: After both models finish (or time out), the Arena reveals two sandboxed live viewports side-by-side. The user reads the telemetry HUD, clicks Winner, hears the success cue, sees ELO update instantly.

**Independent Test**: Inject two pre-generated `DuelResult` records → fetch `GET /api/duels/{id}` → verify both sandboxed viewports render → verify HUD shows all telemetry fields → click Winner → verify ELO shifts are returned and displayed → verify success audio plays and losing viewport is dimmed.

### Implementation for User Story 2

- [X] T060 [P] [US2] Create Application use case `src/PoLocalCompare.Application/Duels/GetDuel/GetDuelQuery.cs` + `GetDuelHandler.cs` — fetches Duel + two DuelResults from repositories; maps to `DuelDto` with full `htmlOutputRaw`; returns null if not found
- [X] T061 [US2] Implement `GET /api/duels/{duelId}` in `src/PoLocalCompare.Api/Endpoints/DuelsEndpoints.cs` — wire to `GetDuelHandler`; 200 or 404; include OpenAPI metadata
- [X] T062 [US2] Create Application use case `src/PoLocalCompare.Application/Duels/RecordVerdict/RecordVerdictCommand.cs` + `RecordVerdictHandler.cs` — validates verdict not already set (409 if set); calls `EloCalculator.Calculate(Ra, Rb, K, outcome)`; updates both Model entities' `CurrentElo`, `DuelCount`, `WinCount`; persists updated Duel (Verdict set), both Models, two `EloRecord` snapshots; returns `VerdictResponseDto` with ELO deltas; include `// SOLID: Single Responsibility — verdict recording coordinates ELO + persistence only` comment
- [X] T063 [US2] Implement `POST /api/duels/{duelId}/verdict` in `src/PoLocalCompare.Api/Endpoints/DuelsEndpoints.cs` — wire to `RecordVerdictHandler`; 200, 409, 422 per contracts/api.md
- [X] T064 [US2] Implement GreenStats calculation in `src/PoLocalCompare.Domain/Services/GreenStatsCalculator.cs` (pure arithmetic — no I/O, no external deps; belongs in Domain per Onion Architecture, same rationale as `EloCalculator` in T028) — `ComputeEnergyWh(tdpWatts, totalDurationMs)`, `ComputeEnergyCostUsd(energyWh, rateUsd)`, `ComputeGreenScore(tokenCount, energyWh)`; called by `DuelExecutionService` when persisting local model `DuelResult`; include `// SOLID: Single Responsibility` comment
- [X] T065 [US2] Implement character density calculation in `DuelExecutionService` — strip HTML comments + collapse whitespace, divide non-whitespace count by total byte length; store as `CharacterDensityRatio` in `DuelResult`
- [X] T066 [P] [US2] Create `src/Client/PoLocalCompare.Client/Components/SandboxedViewport.razor` — renders `<iframe srcdoc="@HtmlContent" sandbox="allow-scripts" title="@ModelName" style="width:100%;height:100%;border:none;">` with `sandbox="allow-scripts"` (permits JS execution required by generated HTML apps; deliberately omits `allow-same-origin` to maintain XSS isolation — scripts cannot access parent frame or cookies); applies CSS filter desaturation when `IsLoser=true`; exposes `HtmlContent`, `ModelName`, `IsLoser` parameters
- [X] T067 [P] [US2] Create `src/Client/PoLocalCompare.Client/Components/TelemetryHud.razor` — semi-transparent overlay showing: token velocity, generation time, warm-up time, character density; energy Wh + cost (local models); API cost (remote models); `IsFailure` badge + `FailureReason` if applicable; uses conditional Radzen components
- [X] T068 [US2] Create `src/Client/PoLocalCompare.Client/Pages/Arena.razor` — fetches `GET /api/duels/{duelId}` on load; renders two `SandboxedViewport` + `TelemetryHud` pairs; desktop: CSS Grid 2-column; mobile (<1024px): vertical CSS scroll-snap stack; "Winner Left" / "Winner Right" / "Re-Challenge" `RadzenButton` row at bottom; on winner click: call `POST /api/duels/{id}/verdict`, receive ELO delta, call `AudioService.PlaySuccess()`, set `IsLoser=true` on losing viewport, display ELO shift badges; on Re-Challenge: call `POST /api/duels/{id}/verdict` to persist result then navigate via `NavigationManager.NavigateTo($"/war-room?prompt={Uri.EscapeDataString(prompt)}&leftModelId={leftId}&rightModelId={rightId}")` — WarRoom reads these query parameters on `OnInitializedAsync` per T056
- [X] T069 [US2] Add `MockDataBanner` to `Arena.razor` (displayed when `Features:UseRealAi=false`)

**Checkpoint**: Arena renders both sandboxed viewports; HUD displays all telemetry fields for both local and remote models; clicking Winner updates ELO and plays audio; losing viewport is visually dimmed; Re-Challenge returns to War Room pre-filled.

---

## Phase 5: User Story 3 — Track and Compare Models on the Leaderboard (Priority: P3)

**Goal**: Leaderboard shows all models ranked by ELO with sparklines, Kill List per model, and sortable by Green Score.

**Independent Test**: Seed 3+ models with 5+ duel history records → verify ELO ranking is correct → verify sparklines show last 20 data points → click a model and verify Kill List shows correct win/loss per opponent → sort by Green Score and verify reordering is independent of ELO rank.

### Implementation for User Story 3

- [X] T070 [P] [US3] Create Application use case `src/PoLocalCompare.Application/Leaderboard/GetLeaderboard/GetLeaderboardQuery.cs` + `GetLeaderboardHandler.cs` — fetches all models from `IModelRepository`; fetches last 20 ELO history records per model from `IEloHistoryRepository.GetLast20(modelId)` for sparkline data; accepts `SortBy` param (`Elo` | `GreenScore`); maps to `LeaderboardEntryDto[]` with `eloSparkline` array
- [X] T071 [P] [US3] Create Application use case `src/PoLocalCompare.Application/Leaderboard/GetKillList/GetKillListQuery.cs` + `GetKillListHandler.cs` — loads all `EloRecord`s for a given `modelId` from `IEloHistoryRepository`; groups by `OpponentModelId`; aggregates Win/Loss counts + last duel; returns `HeadToHead[]`
- [X] T072 [US3] Implement `GET /api/leaderboard` (with `?sortBy=` support) and `GET /api/leaderboard/{modelId}/killlist` in `src/PoLocalCompare.Api/Endpoints/LeaderboardEndpoints.cs` — wire to handlers; include OpenAPI metadata; populate `leaderboard.http` request file
- [X] T073 [P] [US3] Create `src/Client/PoLocalCompare.Client/Components/EloSparkline.razor` — renders inline SVG polyline from `double[]` ELO values; scales to component bounds; green line colour (#00ff00 or equivalent); 20 data points max; no external charting library (pure SVG to avoid CDN dependency in embedded component)
- [X] T074 [US3] Create `src/Client/PoLocalCompare.Client/Pages/Leaderboard.razor` — fetches `GET /api/leaderboard`; Radzen `RadzenDataGrid` with columns: Rank, Model Name, ELO, Duels, W/L, Green Score Avg, Sparkline (`EloSparkline` component); sortable header click toggles between ELO and Green Score sort (calls API with `?sortBy=` param); clicking a row opens Kill List detail panel (`RadzenPanel` or inline expand); each Kill List row shows opponent name, W/L, last duel date
- [X] T075 [US3] Add `MockDataBanner` to `Leaderboard.razor`

**Checkpoint**: Leaderboard renders sorted model list; sparklines display; clicking a model shows Kill List; sorting by Green Score reorders correctly (remote models with null green score sort to bottom).

---

## Phase 6: User Story 4 — Browse Lab Archive and Export Reports (Priority: P4)

**Goal**: Append-only duel history viewable in reverse chronological order; past app outputs re-renderable; one-click HTML Lab Report export.

**Independent Test**: Seed 5 duel records → verify Archive lists them newest-first with correct metadata → click a duel and verify telemetry + ELO shift displays → click Re-render and verify sandboxed viewports load stored HTML → click Export and verify downloaded file is self-contained HTML rendering correctly offline.

### Implementation for User Story 4

- [X] T076 [P] [US4] Create Application use case `src/PoLocalCompare.Application/Duels/ListDuels/ListDuelsQuery.cs` + `ListDuelsHandler.cs` — queries `IDuelRepository.ListAsync(limit, beforeMonth)` returning duel summaries (no HTML output) in reverse chronological order; maps to `DuelSummaryDto[]`
- [X] T077 [US4] Implement `GET /api/duels` with `?limit` and `?before` pagination in `DuelsEndpoints.cs` — wire to `ListDuelsHandler`; include OpenAPI metadata; update `duels.http`
- [X] T078 [US4] Create Application use case `src/PoLocalCompare.Application/Archive/ExportLabReport/ExportLabReportCommand.cs` + `ExportLabReportHandler.cs` — loads Duel + DuelResults + EloHistory records; calls `ILabReportRenderer.RenderAsync(duel, results, eloShifts)`; returns HTML string
- [X] T079 [US4] Implement Razor Lab Report renderer `src/PoLocalCompare.Infrastructure/Reporting/HtmlLabReportRenderer.cs` implementing `ILabReportRenderer` — uses HTML string builder to render self-contained report; inline all CSS; sanitises model HTML outputs (strips `<script>` tags and `on*` attributes); include `// GoF: Template Method — HTML string builder defines report skeleton` comment
- [X] T080 [US4] Create self-contained HTML Lab Report template (inlined into `HtmlLabReportRenderer.cs`) — sections: header (date, models, verdict), raw prompt, telemetry table (all DuelResult fields for both models), ELO shifts (before/after for both models), Source Code panels (winner first, loser second); all CSS inlined; zero external requests
- [X] T081 [US4] Implement `GET /api/duels/{duelId}/report` in `DuelsEndpoints.cs` — wire to `ExportLabReportHandler`; return `text/html` with `Content-Disposition: attachment; filename="lab-report-{duelId}.html"`
- [X] T082 [P] [US4] Extend existing `src/Client/PoLocalCompare.Client/Services/DuelApiClient.cs` (created in T098, Phase 2) with Archive-specific methods: `ListDuelsAsync(int limit, string? before)` and `DownloadReportAsync(string duelId)` returning the raw HTML bytes for browser-triggered download
- [X] T083 [US4] Create `src/Client/PoLocalCompare.Client/Pages/Archive.razor` — fetches `GET /api/duels`; Radzen `RadzenDataGrid` listing duels in reverse chronological order with columns: Date, Prompt Summary (truncated), Left Model, Right Model, Verdict badge (green/red); clicking a row expands detail showing full telemetry table + ELO shifts; "Re-render" button loads both model HTML outputs into two `SandboxedViewport` components inline; "Export Lab Report" button calls `GET /api/duels/{id}/report` and triggers browser download; pagination support via `?before=` cursor
- [X] T084 [US4] Add `MockDataBanner` to `Archive.razor`

**Checkpoint**: Archive lists duels newest-first; selecting a duel shows telemetry + ELO delta; Re-render loads stored HTML into sandboxed viewports; Export downloads a self-contained HTML file that renders correctly when opened with no internet connection.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: OLED theme consistency, responsive layout, nav, dev-mode error surfaces, and LLMDOCS finalization.

- [ ] T085 Create `src/Client/PoLocalCompare.Client/wwwroot/css/app.css` — OLED Black theme: `background: #000000`; colour palette: white/light-grey text, `#22c55e` green (success/high-perf), `#eab308` yellow (warning/average), `#ef4444` red (failure/high-cost); responsive CSS Grid (≥1024px) + CSS scroll-snap vertical stack (<1024px)
- [ ] T086 Create `src/Client/PoLocalCompare.Client/Shared/NavMenu.razor` — navigation links to War Room, Leaderboard, Archive; Radzen `RadzenPanelMenu`; collapses to hamburger on mobile; OLED Black styled
- [ ] T087 Implement dev-mode error surfacing in `src/Client/PoLocalCompare.Client/Shared/MainLayout.razor` — catch unhandled exceptions; if `ASPNETCORE_ENVIRONMENT=Development`, render full exception message + stack trace in a red panel at the bottom of the page; production: generic error message only
- [ ] T088 Add structured error handling to all API endpoints: global exception handler middleware in `Program.cs`; logs full exception with `CorrelationId`, `Environment`, `UserId=anonymous`; returns `application/problem+json` (RFC 7807) in production; includes stack trace in Development
- [ ] T089 Add `Content-Security-Policy: frame-ancestors 'self'` header to all Arena and Archive responses (anti-clickjacking for iframe viewport pages)
- [ ] T090 [P] Write unit tests `tests/unit/PoLocalCompare.Unit.Tests/Domain/EloCalculatorTests.cs` — test standard Elo formula for win, loss, draw-not-supported; edge cases: sub-1-point shift displayed to 1dp; equal-rating expected score = 0.5; K=32 and K=16 parametrised
- [ ] T091 [P] Write unit tests `tests/unit/PoLocalCompare.Unit.Tests/Application/RecordVerdictTests.cs` — mock repositories; verify ELO updated for both models; verify 409 on duplicate verdict; verify `EloRecord` created for each model
- [ ] T092 [P] Write integration tests `tests/integration/PoLocalCompare.Integration.Tests/DuelsEndpointTests.cs` — `WebApplicationFactory` with Testcontainers Azurite; `POST /api/duels` → `POST /api/duels/{id}/verdict` → `GET /api/leaderboard` full flow; `Features:UseRealAi=false` mock AI responses
- [ ] T093 [P] Write integration tests `tests/integration/PoLocalCompare.Integration.Tests/LeaderboardTests.cs` — seed 3 models with 5 duels each; verify ELO ranking order; verify Green Score sort reorders correctly; verify Kill List counts
- [ ] T094 [P] Write E2E Playwright test `tests/e2e/PoLocalCompare.E2E/war-room.spec.ts` — headed mode; load War Room; verify Commence disabled with no selection; select models + enter prompt; click Commence; verify audio fires (check `AudioContext` state); verify Processing page shows two panels
- [ ] T095 [P] Write E2E Playwright test `tests/e2e/PoLocalCompare.E2E/arena.spec.ts` — mock duel complete; verify both viewports render; verify HUD fields present; click Winner; verify ELO badge shown; verify loser viewport has dimmed CSS
- [ ] T096 [P] Finalise `LLMDOCS/README.md` — document solution structure, entry points, key architectural decisions; cross-reference `plan.md`, `data-model.md`, `contracts/api.md`; keep under 300 lines
- [ ] T097 Run `quickstart.md` validation checklist end-to-end and verify all 12 items pass; fix any gaps discovered

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies — start immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1 completion — **BLOCKS all user stories**.
- **Phase 3 (US1)**: Depends on Phase 2 — can start as soon as Foundational completes.
- **Phase 4 (US2)**: Depends on Phase 2 + Phase 3 (needs `DuelExecutionService` and `DuelResult` persistence).
- **Phase 5 (US3)**: Depends on Phase 2 + Phase 4 (needs verdict + ELO records to exist).
- **Phase 6 (US4)**: Depends on Phase 2 — Archive and export are independently implementable alongside US2/US3.
- **Phase 7 (Polish)**: Depends on all user story phases — implements cross-cutting concerns.

### User Story Dependencies

- **US1 (P1)**: Can start after Phase 2 — no dependency on other stories.
- **US2 (P2)**: Depends on US1's `DuelExecutionService` (T047) for `DuelResult` persistence — starts after T047 completes.
- **US3 (P3)**: Depends on US2's `RecordVerdictHandler` (T062) creating `EloRecord`s — starts after T062 completes.
- **US4 (P4)**: Depends on Phase 2 only — `ListDuels` and `ExportLabReport` are independent of US2/US3; can be developed in parallel with US2+US3.

### Within Each Story

- Domain entities → Application interfaces → Infrastructure repositories → API endpoints → Client components/pages
- `SandboxedViewport` and `TelemetryHud` (T066, T067) are parallelisable with each other.
- All `[P]`-marked tasks within a phase can run in parallel if on different files.

### Parallel Opportunities

- All Phase 1 Setup tasks T001–T021 can run in parallel after T001 (sln) and T003–T004 (build props).
- Phase 2: T022–T031 (Domain + Interfaces + Shared DTOs) all parallelisable. T032–T035 (Repositories) parallelisable. T038–T041 parallelisable.
- Phase 3: T050–T054 (client JS + services) fully parallelisable with each other. T042–T043 parallelisable.
- Phase 4: T066–T067 parallelisable. T060 parallelisable with T066.
- Phase 5: T070–T071 parallelisable. T073 parallelisable with T070.
- Phase 6: T076, T078, T082 all parallelisable.
- Phase 7: All test tasks (T090–T095) fully parallelisable.

---

## Parallel Example: User Story 1

```bash
# After Phase 2 Foundational completes, these can run simultaneously:
# Track A — Server use cases
T042 RegisterModelCommand + Handler
T043 ListModelsQuery + Handler
T045 CommenceDuelCommand + Handler

# Track B — Client JS infrastructure (no server dependency)
T050 webllm-worker.js
T051 WebLlmService.cs
T052 audio.js + .wav assets
T053 AudioService.cs
T054 SignalRDuelClient.cs

# Track C — Server infrastructure (no client dependency)
T047 DuelExecutionService
T048 DuelHub SignalR
T049 FoundryInferenceProxy

# Then sequentially once Tracks converge:
T055 ModelCard.razor → T056 WarRoom.razor → T057 ProcessingPanel.razor → T058 Processing.razor
```

---

## Implementation Strategy

**MVP = Phase 1 + Phase 2 + Phase 3 (User Story 1)**.

This delivers: scaffolded solution, persistence layer, health/diag, model registry API, duel launch API, SignalR hub, War Room UI, Processing UI, and the WebLLM Web Worker integration. The full benchmarking loop is runnable end-to-end.

Add US2 (Arena + Verdict + ELO) to complete the core judging loop. US3 (Leaderboard) and US4 (Archive + Export) are additive value layers that can be delivered in any order after the core loop works.


