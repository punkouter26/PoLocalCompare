# AGENT.MD — PoLocalCompare AI Coding Agent Context

> **Read this first.** This file is the primary context document for any AI coding agent working in this repository. Start here before touching any code.

---

## 1. What Is This Project?

**PoLocalCompare** is a real-time LLM benchmarking platform that pits **local browser-based LLMs** (WebLLM / WebGPU) against **remote cloud models** (Azure AI Foundry) and **Ollama local models** in timed HTML-generation duels. A human judge picks the winner. An Elo (K=32) rating system tracks relative model performance across all duels.

- **Live URL:** https://polocalcompare.azurewebsites.net
- **Solution file:** `PoLocalCompare.slnx`
- **Master prefix rule:** Every namespace, Azure resource, and Aspire resource uses `PoLocalCompare` as its prefix.

### The Core Loop
1. User selects two models (Local WebLLM / Remote Azure Foundry / Ollama)
2. User enters a prompt — models race to generate HTML
3. Both outputs stream live via SignalR
4. Human or Auto-Judge (GPT-4.1 Nano) picks the winner
5. Elo ratings update, stored in Azure Table Storage

---

## 2. Tech Stack

| Layer | Technology |
|---|---|
| **Runtime** | .NET 10 (pinned via `global.json`) |
| **Language** | C# 14 (`LangVersion=14` in `Directory.Build.props`) |
| **Client** | Blazor WASM hosted in `PoLocalCompare.Api` |
| **UI library** | Radzen Blazor |
| **In-browser AI** | WebLLM (WebGPU) via JS interop |
| **Remote AI** | Azure AI Foundry (Azure OpenAI) |
| **Local AI** | Ollama HTTP (`http://localhost:11434`) |
| **Real-time** | SignalR (`DuelHub`) |
| **Storage (local)** | Azurite Docker container (ports 10000–10002) |
| **Storage (Azure)** | Azure Table Storage |
| **Secrets** | Azure Key Vault (`kv-poshared`, shared, **access-policy mode**) + App Service settings fallback |
| **Identity** | System-assigned Managed Identity (per §5.4); Bicep grants Storage Table/Blob RBAC + kv-poshared access |
| **Auth** | Microsoft OAuth (Prod) + GUEST mode (Dev/Test) |
| **Logging** | Serilog → File + Console + App Insights |
| **Telemetry** | OpenTelemetry → Azure Monitor |
| **Versioning** | MinVer (Git tag driven, prefix `v`) |
| **Styling** | Cyberpunk/OLED dark theme, 4px grid, CSS clamp() |

---

## 3. Project Structure (Vertical Slice Architecture)

> **Architecture note:** This solution uses **Vertical Slice Architecture (VSA)** per the global
> `Po*` mandate (§2.1). The former Clean/Onion layout (Domain/Application/Infrastructure projects)
> was collapsed into `PoLocalCompare.Api` on 2026-07-06 — see
> [docs/adr/0002-migrate-to-vertical-slices.md](docs/adr/0002-migrate-to-vertical-slices.md).
> New server code goes in `src/PoLocalCompare.Api/Features/<Feature>/` (flat: endpoint + handlers +
> entities + repository together); only genuinely cross-slice code goes in `Common/`.


```
PoLocalCompare.slnx
├── src/
│   ├── PoLocalCompare.Api/             ← ASP.NET Minimal API, SignalR, hosted Blazor WASM (VSA)
│   │   ├── Features/
│   │   │   ├── Duels/                  ← endpoints, commence/get/list/record-verdict handlers,
│   │   │   │                             Duel + DuelResult entities & repositories,
│   │   │   │                             DuelExecutionService, AutoJudgeService, DuelHub
│   │   │   ├── Leaderboard/            ← endpoints, handlers, EloRecord, EloCalculator, EloHistoryRepository
│   │   │   ├── Models/                 ← endpoints, handlers, Model entity, ModelRepository, ModelSeeder
│   │   │   ├── Archive/                ← lab-report export handler + HTML renderer
│   │   │   ├── Ollama/                 ← status/pull endpoints
│   │   │   ├── Lobby/                  ← LobbyHub (SignalR)
│   │   │   └── Diagnostics/            ← /health, /api/diag/*, E2E helpers
│   │   ├── Common/
│   │   │   ├── Domain/                 ← cross-slice calculators & value objects
│   │   │   ├── Inference/              ← IRemoteInferenceProxy + Foundry/Ollama typed-HttpClient proxies
│   │   │   ├── Background/             ← background task queue + hosted service
│   │   │   ├── KeyVault/               ← prefix-scoped Key Vault configuration
│   │   │   ├── Persistence/            ← Azurite table bootstrap
│   │   │   └── Telemetry/              ← RateLimitedSampler (§6.3)
│   │   ├── Auth/                       ← BFF cookie session + Microsoft OIDC + dev fake scheme
│   │   ├── Pages/                      ← /diag Razor page
│   │   ├── GlobalUsings.cs             ← all slice namespaces project-wide
│   │   ├── Program.cs                  ← Composition root
│   │   ├── ServiceCollectionExtensions.cs
│   │   └── InfrastructureServiceExtensions.cs
│   ├── PoLocalCompare.Shared/          ← DTOs and Enums shared between WASM and API
│   │   ├── DTOs/                       ← DuelDto, ModelDto, LeaderboardEntryDto, VerdictRequestDto, etc.
│   │   └── Enums/
│   └── Client/
│       └── PoLocalCompare.Client/      ← Blazor WASM (hosted by Api)
│           ├── Pages/                  ← Home, WarRoom, Processing, Arena, Leaderboard,
│           │                             Archive, LocalModelLab, Login
│           ├── Components/
│           ├── Services/               ← DuelApiClient, BffAuthenticationStateProvider,
│           │                             WebLlmService, SignalRDuelClient, AudioService,
│           │                             InferenceSessionStore, CorrelationHandler
│           └── Layout/
├── tests/
│   ├── PoLocalCompare.UnitTests/       ← xUnit + Moq (pure logic)
│   ├── PoLocalCompare.IntegrationTests/← Testcontainers (Azurite)
│   ├── PoLocalCompare.E2EAPI/          ← Black-box HTTP journey tests (C#)
│   └── PoLocalCompare.E2EUI/           ← Playwright for .NET (C#)
├── docs/                               ← PRD.md, adr/, architecture diagrams (.mmd), DataModel
├── SCRIPTS/                            ← setup.ps1, download-models.py, cleanup-models.py
├── infra/                              ← Bicep (main.bicep / keyvault-access.bicep / main.json)
├── Directory.Build.props               ← Global: TreatWarningsAsErrors, Nullable, LangVersion=14
├── Directory.Packages.props            ← Central Package Management
├── global.json                         ← .NET 10 SDK pin
└── docker-compose.yml                  ← Azurite container
```

---

## 4. Key Architecture Decisions

### 4.1 Vertical Slice Architecture
- Each `Features/<Feature>/` folder owns its endpoint, handlers, entities, and repository (flat).
- `Common/` holds only genuinely cross-slice code; `GlobalUsings.cs` exposes every slice namespace.
- Server authorization is deny-by-default: `FallbackPolicy = RequireAuthenticatedUser` (§4.5);
  public endpoints opt out explicitly with `.AllowAnonymous()`.
- Table Storage writes are idempotent + ETag-safe (§5.5): creates swallow 409, updates are
  If-Match conditional (entities carry `ETag`), and duel writers re-read + reapply on 412.
- Telemetry (§6.3): 100% sampling outside Production; Production caps traces via
  `Common/Telemetry/RateLimitedSampler` (`Telemetry:MaxTracesPerSecond`, default 10/s);
  Error+ log events ship to App Insights unsampled through a Serilog sink.
- HttpClients (§5.4/§5.6): Foundry/Ollama proxies are typed clients with retry-only resilience
  (no per-attempt timeout — it would abort SSE streams); named "Foundry"/"Ollama"/"OllamaStatus"
  clients cover the remaining callers. Non-dev startup fail-fasts on unreachable Table Storage.
- The client stamps `X-Session-ID`/`X-Correlation-ID` on every request (§6.9) via `CorrelationHandler`.

### 4.2 Storage Strategy
```csharp
// Local dev → Azurite Docker
"UseDevelopmentStorage=true"

// Azure → Azure Table Storage via Key Vault
ConnectionStrings__AzureTableStorage = kv-poshared secret
```
Dynamic switching is automatic based on `ASPNETCORE_ENVIRONMENT`.

### 4.3 AI Model Categories
| Category | When Available | Notes |
|---|---|---|
| **Remote** | Always (Azure AI Foundry) | Requires `AzureAiFoundry:ApiKey` or Managed Identity |
| **Browser** | When browser has WebGPU | WebLLM, runs in WASM, first-run download |
| **Ollama** | Only when running locally | `http://localhost:11434`, checked at runtime |

### 4.4 Authentication (BFF)
The API is a **Backend-for-Frontend**: it owns the OIDC code flow and the session. The Blazor WASM
client holds **no tokens** — the session lives in an encrypted `HttpOnly`, `SameSite=Strict` cookie
(`PoLocalCompare.Session`) that the browser sends automatically on same-origin calls.

- **Server routes** (`src/PoLocalCompare.Api/Auth/BffAuthentication.cs`):
  `GET /auth/login/microsoft` (OIDC challenge), `GET /auth/login/fake` (dev/test guest bypass),
  `POST /auth/logout`, `GET /auth/me` (server auth state for the SPA).
- **Client** (`BffAuthenticationStateProvider`) reads `/auth/me`; `App.razor` gates every routed page
  with `<AuthorizeRouteView>` (`@attribute [Authorize]` in `_Imports.razor`), rendering `Login` when
  unauthenticated. So the user **must** sign in before the home page.
- **Dev:** Microsoft OAuth **and** "Continue as Guest". **Prod:** Microsoft OAuth only (guest hidden).
  **Test:** the `FakeAuthHandler` maps `X-Fake-User` / `X-Fake-Roles` headers to a principal — registered
  only outside Production (throws if constructed in Production).
- API endpoint groups and SignalR hubs are protected with `.RequireAuthorization()`; `/health`, `/diag`,
  `/auth/*`, and static assets stay anonymous.
- **Config:** `AzureAd:{Authority,ClientId,AllowedTenants}` in the API appsettings; `AzureAd:ClientSecret`
  from Key Vault / App Service settings. Issuer is validated against `AllowedTenants`.

### 4.5 Real-Time (SignalR)
- `DuelHub` at `/hubs/duel`
- Client: `SignalRDuelClient.cs` in WASM
- Token streaming, model status updates, auto-judge results all flow through SignalR

### 4.6 Elo System
- K-factor: 32, Starting rating: 1200
- Updated after each completed duel (verdict recorded)

---

## 5. Critical Configuration Keys

| Key | Primary Source | Fallback |
|---|---|---|
| `KeyVault:Uri` | App Service setting | `appsettings.json` |
| `ApplicationInsights:ConnectionString` | Key Vault | App Service setting |
| `ConnectionStrings:AzureTableStorage` | Key Vault | App Service setting |
| `ConnectionStrings:AzureBlobStorage` | Key Vault | App Service setting |
| `AzureAiFoundry:ApiKey` | Key Vault | `dotnet user-secrets` |
| `Elo:KFactor` | `appsettings.json` | — (default 32) |
| `Duel:TimeLimitSeconds` | `appsettings.json` | — (default 300) |
| `Ollama:BaseUrl` | `appsettings.json` | — (`http://localhost:11434`) |

---

## 6. Running Locally

```bash
# 1. Start Azurite
docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite

# 2. (Optional) Download browser model assets
python SCRIPTS/download-models.py

# 3. (Optional) Set Azure AI Foundry key
dotnet user-secrets set "AzureAiFoundry:ApiKey" "your-key" --project src/PoLocalCompare.Api

# 4. Run the API (serves Blazor WASM at https://localhost:5001)
dotnet run --project src/PoLocalCompare.Api --launch-profile https
```

**Fixed ports:** HTTP `5000`, HTTPS `5001`

---

## 7. Development Standards (Quick Reference)

> Full standards are in [docs/PRD.md](docs/PRD.md) and [docs/adr/0001-clean-architecture-over-vertical-slices.md](docs/adr/0001-clean-architecture-over-vertical-slices.md).

- **No AOT.** `RunAOTCompilation=false` — keeps dev builds fast.
- **Warnings are errors.** `TreatWarningsAsErrors=true` globally.
- **Nullable enabled.** No null reference warnings tolerated.
- **SOLID + GoF.** Use `<remarks>` XML tags to explain pattern choices.
- **Zero-waste C#.** Delete unused files immediately. Optimize for human readability and LLM context.
- **Diagnostics endpoints:** `/health` (JSON, automated pings) and `/diag` (dev+prod hidden page). **No UI links to these.**
- **Mock visibility:** When `Features:UseRealAi` is disabled (AI responses simulated), display `USING MOCK DATA` in the top nav (see `NavMenu.razor`).
- **Audio:** Web Audio API for in-app sounds.

---

## 8. Testing Strategy

| Test Type | Project | Notes |
|---|---|---|
| Unit | `tests/PoLocalCompare.UnitTests` | Pure logic, mock data (no real AI) |
| Integration | `tests/PoLocalCompare.IntegrationTests` | Testcontainers (Azurite), fake-auth header, dev env |
| E2E API | `tests/PoLocalCompare.E2EAPI` | Black-box HTTP journeys over Testcontainers + fake auth |
| E2E UI | `tests/PoLocalCompare.E2EUI` | C# Playwright; drives a running app via `BASE_URL` |

- Integration / E2E-API tests run in a **non-Production** host so the `X-Fake-User` guest bypass works.
- E2E-UI needs a running app and `playwright install`; it is **not** run in CI (see policy below).
- Tests are intentionally **not** executed in CI/CD — the pipeline only builds, publishes, and deploys.

---

## 9. Deployment (Azure)

```bash
# Validate and preview
azd provision --preview

# Deploy
azd up
```

- **App resources RG:** `PoLocalCompare` (`westus2`) — holds storage `polocalcomparedevsa` and the F1 plan `asp-PoLocalCompare-f1`.
- **Shared platform RG:** `PoShared` — Key Vault `kv-poshared`, App Insights, and the shared identity live here.
- **Key Vault:** `kv-poshared` (shared across Po* projects). **Authorization mode: access policies, not RBAC.**
- **Managed Identity:** **System-assigned** on the App Service (standards §5.4, reconciled 2026-07-06). Bicep grants it Storage Table/Blob Data Contributor on `polocalcomparedevsa` (the app connects via `AzureStorage__AccountName`, no shared-key string) and both a `Key Vault Secrets User` role assignment and a get/list access policy on `kv-poshared` (`infra/keyvault-access.bicep`) — the policy is what authorizes today; the role becomes effective if the vault ever moves to RBAC mode. `AZURE_CLIENT_ID` is no longer set.
- **App-scoped secrets** use the `PoLocalCompare--` prefix (e.g. `PoLocalCompare--AzureAiFoundry--ApiKey`, `PoLocalCompare--ConnectionStrings--AzureTableStorage`); `PrefixKeyVaultSecretManager("PoLocalCompare")` strips the prefix. **A `PoLocalCompare--AzureAd--ClientSecret` must be added for BFF OIDC** (only the unprefixed shared `AzureAd--ClientSecret` exists today, which the prefix manager won't load).

> **Bicep status (reconciled 2026-07-06):** `infra/main.bicep` creates a Windows App Service
> (`app-polocalcompare-win`, .NET 10, **SystemAssigned** identity) on the existing `asp-PoLocalCompare-f1`
> plan, assigns Storage Table/Blob Data Contributor on `polocalcomparedevsa`, and grants Key Vault access
> via the `infra/keyvault-access.bicep` module in `PoShared`. Two operational notes: (1) the CI workflow
> only runs Bicep when the App Service is missing — the identity switch needs a **one-time manual**
> `az deployment group create -g PoLocalCompare --template-file infra/main.bicep`; (2) add the
> `PoLocalCompare--AzureAd--ClientSecret` secret to `kv-poshared` for BFF OIDC.

---

## 10. Available Copilot Skills

These skills are installed globally (`~/AppData/Roaming/Code/User/prompts/skills/`) and can be invoked in Copilot Chat:

| Phase | Skill | When to Use |
|---|---|---|
| Day 1 | `acquire-codebase-knowledge` | Map the codebase before making changes |
| Design | `architecture-blueprint-generator` | Define/review layer boundaries |
| Design | `folder-structure-blueprint-generator` | Validate file placement |
| Build | `dotnet-best-practices` | Writing any C# class (DI, async, config) |
| Build | `dotnet-design-pattern-review` | After writing a service or domain object |
| Build | `autoresearch` | Optimize measurable metrics (test pass rate, build size) |
| Security | `security-review` | Full OWASP scan before merging to `main` |
| Deploy | `appinsights-instrumentation` | Wire telemetry before going live |
| Deploy | `azure-deployment-preflight` | Run immediately before `azd up` |
| Operate | `azure-resource-health-diagnose` | When something breaks in Azure |
| Document | `create-readme` | When the project is stable enough to describe |
| Milestone | `repo-story-time` | End of a release — generates `REPOSITORY_SUMMARY.md` |

---

## 11. Important Files to Know

| File | Purpose |
|---|---|
| [Program.cs](src/PoLocalCompare.Api/Program.cs) | Composition root, Key Vault bootstrap, Serilog, OTel |
| [ServiceCollectionExtensions.cs](src/PoLocalCompare.Api/ServiceCollectionExtensions.cs) | DI registrations |
| [InfrastructureServiceExtensions.cs](src/PoLocalCompare.Api/InfrastructureServiceExtensions.cs) | Storage clients, typed HttpClients + resilience, repository DI |
| [DuelHub.cs](src/PoLocalCompare.Api/Features/Duels/DuelHub.cs) | SignalR hub |
| [DuelExecutionService.cs](src/PoLocalCompare.Api/Features/Duels/DuelExecutionService.cs) | Core duel orchestration |
| [AutoJudgeService.cs](src/PoLocalCompare.Api/Features/Duels/AutoJudgeService.cs) | GPT-4.1 Nano auto-verdict |
| [AzuriteSetup.cs](src/PoLocalCompare.Api/Common/Persistence/AzuriteSetup.cs) | Local storage bootstrap |
| [BffAuthentication.cs](src/PoLocalCompare.Api/Auth/BffAuthentication.cs) | BFF OIDC flow, session cookie, dev fake-auth guard |
| [BffAuthenticationStateProvider.cs](src/Client/PoLocalCompare.Client/Services/BffAuthenticationStateProvider.cs) | Client auth state from `/auth/me` |
| [WebLlmService.cs](src/Client/PoLocalCompare.Client/Services/WebLlmService.cs) | WebGPU browser inference |
| [docs/PRD.md](docs/PRD.md) | Full product requirements |
| [SCRIPTS/setup.ps1](SCRIPTS/setup.ps1) | First-run machine setup |

---

## 12. LLM Workflow Rules

When asked to make changes to this codebase:

1. **Assumptions first:** If a task is unclear, stop and list assumptions before coding.
2. **Pro/Con for major changes:** One "Pro" (e.g., "Removes legacy boilerplate") and one "Con" (e.g., "Increases DLL count by 1").
3. **Zero-waste:** Delete unused code/files immediately. Do not leave commented-out code.
4. **Pattern remarks:** Use `<remarks>` XML tags on non-obvious pattern choices.
5. **No AOT.** Never enable `RunAOTCompilation=true`.
6. **Security:** OWASP Top 10 compliance. No secrets in code or logs.
7. **Ports:** Never change 5000/5001 without explicit instruction.
