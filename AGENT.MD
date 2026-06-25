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
| **Identity** | User-Assigned Managed Identity `mi-poshared-containerapps` (shared `PoShared` RG) |
| **Auth** | Microsoft OAuth (Prod) + GUEST mode (Dev/Test) |
| **Logging** | Serilog → File + Console + App Insights |
| **Telemetry** | OpenTelemetry → Azure Monitor |
| **Versioning** | MinVer (Git tag driven, prefix `v`) |
| **Styling** | Cyberpunk/OLED dark theme, 4px grid, CSS clamp() |

---

## 3. Project Structure (Onion Architecture)

> **Architecture note:** This solution uses Clean/Onion layering, deliberately deviating from the global
> Vertical Slice Architecture (VSA) mandate. The rationale (and where new code goes) is recorded in
> [docs/adr/0001-clean-architecture-over-vertical-slices.md](docs/adr/0001-clean-architecture-over-vertical-slices.md).
> Do not start a VSA migration without revisiting that ADR.


```
PoLocalCompare.slnx
├── src/
│   ├── PoLocalCompare.Domain/          ← Entities, Value Objects, Domain Services
│   │   ├── Entities/                   ← Duel, DuelResult, EloRecord, Model
│   │   ├── Services/
│   │   └── ValueObjects/
│   ├── PoLocalCompare.Application/     ← Use Cases (CQRS-style handlers), Interfaces
│   │   ├── Duels/                      ← CommenceDuel, GetDuel, ListDuels, RecordVerdict
│   │   ├── Leaderboard/                ← GetLeaderboard, GetKillList
│   │   ├── Archive/
│   │   ├── Interfaces/                 ← IDuelRepository, IModelRepository, IRemoteInferenceProxy, etc.
│   │   └── Models/
│   ├── PoLocalCompare.Infrastructure/  ← External systems, repos, Azurite/Table Storage
│   │   ├── AzureAiFoundry/             ← FoundryInferenceProxy.cs
│   │   ├── Ollama/
│   │   ├── Persistence/                ← AzuriteSetup, ModelSeeder, TableStorage/
│   │   ├── KeyVault/
│   │   └── Reporting/
│   ├── PoLocalCompare.Api/             ← ASP.NET Minimal API, SignalR hub, hosted Blazor WASM
│   │   ├── Endpoints/                  ← DuelsEndpoints, LeaderboardEndpoints, ModelsEndpoints,
│   │   │                                 OllamaEndpoints, HealthEndpoints
│   │   ├── Hubs/                       ← DuelHub.cs (SignalR)
│   │   ├── Services/                   ← AutoJudgeService, DuelExecutionService, BackgroundTaskService
│   │   ├── Infrastructure/
│   │   ├── Pages/
│   │   ├── Program.cs                  ← Composition root
│   │   └── ServiceCollectionExtensions.cs
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
│           │                             InferenceSessionStore
│           └── Layout/
├── tests/
│   ├── unit/PoLocalCompare.Unit.Tests/
│   ├── integration/PoLocalCompare.Integration.Tests/   ← Testcontainers (Azurite)
│   ├── e2e-api/PoLocalCompare.E2EAPI/  ← Black-box HTTP journey tests (C#)
│   └── e2e-ui/PoLocalCompare.E2EUI/    ← Playwright for .NET (C#)
├── docs/                               ← PRD.md, architecture diagrams (.mmd), DataModel
├── SCRIPTS/                            ← setup.ps1, download-models.py, cleanup-models.py
├── infra/                              ← Bicep (main.bicep / main.json)
├── Directory.Build.props               ← Global: TreatWarningsAsErrors, Nullable, LangVersion=14
├── Directory.Packages.props            ← Central Package Management
├── global.json                         ← .NET 10 SDK pin
└── docker-compose.yml                  ← Azurite container
```

---

## 4. Key Architecture Decisions

### 4.1 Onion / Clean Architecture
- **Domain** has zero external dependencies.
- **Application** depends only on Domain.
- **Infrastructure** implements Application interfaces (Repository pattern).
- **Api** is the composition root; it wires everything via DI.

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
| Unit | `tests/unit/PoLocalCompare.Unit.Tests` | Pure logic, mock data (no real AI) |
| Integration | `tests/integration/PoLocalCompare.Integration.Tests` | Testcontainers (Azurite), fake-auth header, dev env |
| E2E API | `tests/e2e-api/PoLocalCompare.E2EAPI` | Black-box HTTP journeys over Testcontainers + fake auth |
| E2E UI | `tests/e2e-ui/PoLocalCompare.E2EUI` | C# Playwright; drives a running app via `BASE_URL` |

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
- **Managed Identity:** **User-Assigned** `mi-poshared-containerapps`, shared from `PoShared`. It already holds a `get`/`list` **access policy** on `kv-poshared`. Storage access is via Storage Table/Blob Data Contributor on the app's storage account. `DefaultAzureCredential` targets it via the `AZURE_CLIENT_ID` app setting.
- **App-scoped secrets** use the `PoLocalCompare--` prefix (e.g. `PoLocalCompare--AzureAiFoundry--ApiKey`, `PoLocalCompare--ConnectionStrings--AzureTableStorage`); `PrefixKeyVaultSecretManager("PoLocalCompare")` strips the prefix. **A `PoLocalCompare--AzureAd--ClientSecret` must be added for BFF OIDC** (only the unprefixed shared `AzureAd--ClientSecret` exists today, which the prefix manager won't load).

> **Bicep status (reconciled 2026-06-24):** `infra/main.bicep` now references only real resources — it
> creates a Windows App Service (`app-polocalcompare-win`, .NET 10) on the existing `asp-PoLocalCompare-f1`
> plan, references the existing `polocalcomparedevsa` storage account, and wires the shared `PoShared`
> resources (`kv-poshared`, App Insights, `mi-poshared-containerapps`) as `existing`. Key Vault uses access
> policies (the shared identity already holds get/list), so no role assignment is managed in Bicep. One
> outstanding prerequisite: add the `PoLocalCompare--AzureAd--ClientSecret` secret to `kv-poshared` for BFF OIDC.
>
> **Deviation note (intentional):** The global standard calls for a *system-assigned* identity; this solution deliberately uses the *shared user-assigned* platform identity so all `Po*` workloads share one principal, avoiding per-app role sprawl.

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
| [InfrastructureServiceExtensions.cs](src/PoLocalCompare.Infrastructure/InfrastructureServiceExtensions.cs) | Infrastructure DI |
| [DuelHub.cs](src/PoLocalCompare.Api/Hubs/DuelHub.cs) | SignalR hub |
| [DuelExecutionService.cs](src/PoLocalCompare.Api/Services/DuelExecutionService.cs) | Core duel orchestration |
| [AutoJudgeService.cs](src/PoLocalCompare.Api/Services/AutoJudgeService.cs) | GPT-4.1 Nano auto-verdict |
| [AzuriteSetup.cs](src/PoLocalCompare.Infrastructure/Persistence/AzuriteSetup.cs) | Local storage bootstrap |
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
