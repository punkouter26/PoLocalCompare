# PoLocalCompare — LLM Duel Arena

## Quick Orientation

PoLocalCompare is a benchmarking platform that pits **local browser-based LLMs** (via WebLLM/WebGPU) against **remote cloud models** (via Azure AI Foundry) in timed HTML-generation duels. An Elo rating system (K=32) tracks relative performance across duels. The judge is always human.

> For the full specification see [specs/001-llm-duel-arena/plan.md](../specs/001-llm-duel-arena/plan.md).

---

## Solution Structure

```
PoLocalCompare.slnx
├─ src/
│  ├─ PoLocalCompare.Domain/          # Entities, Value Objects, Domain Services
│  ├─ PoLocalCompare.Application/     # Use-case handlers, Application interfaces
│  ├─ PoLocalCompare.Infrastructure/  # Azure Table Storage repos, Foundry proxy
│  ├─ PoLocalCompare.Shared/          # DTOs and Enums shared with WASM client
│  ├─ PoLocalCompare.Api/             # ASP.NET Core minimal API + SignalR + Blazor host
│  └─ Client/PoLocalCompare.Client/   # Blazor WASM — 4 pages + Web Worker
├─ tests/
│  ├─ unit/                           # xUnit + Moq unit tests (Domain, Application)
│  ├─ integration/                    # WebApplicationFactory + Testcontainers.Azurite
│  └─ e2e/                            # Playwright TypeScript E2E tests
├─ infra/main.bicep                   # Azure resource definitions (Bicep)
└─ specs/001-llm-duel-arena/          # Design specs, data model, API contracts
```

### Project Responsibilities

| Project | Key Contents |
|---------|-------------|
| `Domain` | `Model`, `Duel`, `DuelResult`, `EloRecord` entities; `EloCalculator` (pure, static) |
| `Application` | Handlers: `CommenceDuelHandler`, `RecordVerdictHandler`, `GetLeaderboardHandler`, `ExportLabReportHandler`; interfaces: `IDuelRepository`, `IModelRepository`, `IEloHistoryRepository` |
| `Infrastructure` | `ModelRepository`, `DuelRepository`, `EloHistoryRepository` (all Azure Table Storage); `FoundryInferenceProxy` (Azure AI Foundry); `HtmlLabReportRenderer` |
| `Shared` | `DuelDto`, `ModelDto`, `LeaderboardEntryDto`, `DuelVerdict` enum, `DuelStatus` enum |
| `Api` | Minimal API endpoints grouped under `/api/`; `DuelHub` (SignalR); `DuelExecutionService` (background fire-and-forget); global RFC 7807 exception handler; CSP headers |
| `Client` | `WarRoom`, `Arena`, `Leaderboard`, `Archive` pages; `DuelApiClient`; `AudioService`; WebLLM Web Worker for in-browser GPU inference |

---

## Entry Points

| Entry point | Description |
|-------------|-------------|
| `src/PoLocalCompare.Api/Program.cs` | Application bootstrapper — DI wiring, middleware pipeline, Serilog, OpenTelemetry, endpoint registration |
| `src/Client/PoLocalCompare.Client/Program.cs` | Blazor WASM host builder — registers `DuelApiClient`, `AudioService`, `SignalRDuelClient`, Radzen services |
| `src/PoLocalCompare.Api/Endpoints/DuelsEndpoints.cs` | All `/api/duels` routes (commence, get, verdict, local-result) |
| `src/PoLocalCompare.Api/Endpoints/LeaderboardEndpoints.cs` | `/api/leaderboard` and `/api/leaderboard/{id}/killlist` |
| `src/PoLocalCompare.Api/Endpoints/ModelsEndpoints.cs` | `/api/models` CRUD |
| `src/PoLocalCompare.Api/Hubs/DuelHub.cs` | SignalR hub; clients join group `duel:{duelId}` to receive live token-stream events |
| `src/PoLocalCompare.Api/Services/DuelExecutionService.cs` | Fire-and-forget task that runs remote Foundry inference concurrently with client WebLLM; 300 s watchdog |

---

## Duel Lifecycle (Data Flow)

1. **War Room** → user selects two models + enters prompt → `POST /api/duels` (202 Accepted, returns `duelId`)
2. Client subscribes to SignalR group `duel:{duelId}`
3. **Remote models**: `DuelExecutionService` calls `FoundryInferenceProxy.RunInferenceAsync()` in the background; streams `ModelStatusUpdate` events via SignalR
4. **Local models**: server sends `StartLocalInference` SignalR event; Blazor WASM starts Web Worker (WebGPU/WebLLM); result POSTed back to `/api/duels/{id}/local-result`
5. Both sides complete → `DuelStatus = Done`; client navigates to `/arena/{duelId}`
6. **Arena** → human reviews dual sandboxed HTML viewports → clicks Winner
7. `POST /api/duels/{id}/verdict` → `RecordVerdictHandler` runs `EloCalculator`, persists `EloRecord` for each model
8. **Leaderboard** → `GET /api/leaderboard?sortBy=Elo|GreenScore` shows live rankings

> See [specs/001-llm-duel-arena/contracts/api.md](../specs/001-llm-duel-arena/contracts/api.md) for the full REST + SignalR contract.

---

## Key Architectural Decisions

### Onion / Clean Architecture
Dependency arrow points inward: `Domain` has zero external deps. `Application` only knows domain interfaces. `Infrastructure` and `Api` depend outward from the core. This keeps the domain logic testable in isolation.

### Azure Table Storage (not a relational DB)
Chosen for simplicity in local dev (Azurite emulator) and serverless scaling on Azure. Four tables: `Models`, `Duels`, `DuelResults`, `EloHistory`. ULID row keys give time-ordering without a sequence generator.

> Entity field definitions: [specs/001-llm-duel-arena/data-model.md](../specs/001-llm-duel-arena/data-model.md)

### Elo Rating (K=32)
Standard chess formula; winner/loser shifts are always zero-sum. Starting rating: 1200. `EloCalculator` is a pure static class with no side effects — tested independently of all infrastructure.

### OLED Black UI Theme
CSS custom properties on `:root` (`--oled-bg: #000000`, `--oled-green: #22c55e`). The client uses an energy-minimising dark palette suitable for AMOLED displays. CSS Grid layout for ≥1024 px; scroll-snap fallback for mobile.

### Error Surfacing Strategy
- **Client**: `ErrorBoundary` in `MainLayout.razor` — dev mode shows full stack trace (`.dev-error-panel`); prod shows generic message (`.prod-error-panel`).
- **Server**: Global `UseExceptionHandler` middleware returns RFC 7807 `application/problem+json`; includes `correlationId`; stack trace only in development.

### Security
- `Content-Security-Policy: frame-ancestors 'self'` — prevents clickjacking.
- Azure Key Vault for secrets (API keys, connection strings); not stored in `appsettings.json`.
- Sandboxed `<iframe>` with `sandbox="allow-scripts"` for model HTML output — prevents escape from the rendered viewport.

---

## Development Setup

1. Prerequisites: .NET 10 SDK, Docker, Node.js 20 LTS (for E2E tests), Edge/Chrome 113+
2. Start Azurite: `docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite`
3. Copy `appsettings.Development.json` and set `AzureAiFoundry:*` keys (or set `Features:UseRealAi=false` to skip)
4. `dotnet run --project src/PoLocalCompare.Api --launch-profile https`
5. Open `https://localhost:5001` — War Room is the home page
6. Scalar API explorer: `https://localhost:5001/scalar`
7. Health check: `https://localhost:5001/health`

> Full checklist: [specs/001-llm-duel-arena/quickstart.md](../specs/001-llm-duel-arena/quickstart.md)

---

## Testing

| Layer | Runner | Location |
|-------|--------|----------|
| Unit | `dotnet test` | `tests/unit/PoLocalCompare.Unit.Tests/` |
| Integration | `dotnet test` + Testcontainers Azurite | `tests/integration/PoLocalCompare.Integration.Tests/` |
| E2E | `npx playwright test` | `tests/e2e/PoLocalCompare.E2E/` |

Key unit tests: `EloCalculatorTests.cs` (pure formula), `RecordVerdictTests.cs` (use-case with Moq repositories).  
Key integration tests: `DuelsEndpointTests.cs` (full POST→verdict→leaderboard flow), `LeaderboardTests.cs` (ELO ranking + Kill List).

---

## Design Patterns in Use

| Pattern | Location |
|---------|----------|
| GoF: Repository | `Infrastructure/Persistence/TableStorage/` — one class per entity |
| GoF: Proxy | `FoundryInferenceProxy.cs` — wraps Azure AI Foundry SDK |
| GoF: Strategy | `DuelExecutionService.cs` — local vs remote inference path selected at runtime |
| GoF: Observer | `DuelHub.cs` — SignalR hub pushes status events to subscribers |
| SOLID: DIP | All application interfaces in `Application/Interfaces/` — Infrastructure implements, Domain owns |
| SOLID: SRP | `EloCalculator.cs` — pure formula only, no persistence or logging |

---

## Further Reading

- [Architecture diagram](architecture.md)
- [API surface reference](api-surface.md)
- [Full spec](../specs/001-llm-duel-arena/spec.md)
- [Research & decisions](../specs/001-llm-duel-arena/research.md)
