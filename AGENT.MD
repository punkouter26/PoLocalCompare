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
| **Secrets** | Azure Key Vault (`kv-poshared`) + App Service settings fallback |
| **Identity** | Managed Identity (Punkouter26 resource group) |
| **Auth** | Microsoft OAuth (Prod) + GUEST mode (Dev/Test) |
| **Logging** | Serilog → File + Console + App Insights |
| **Telemetry** | OpenTelemetry → Azure Monitor |
| **Versioning** | MinVer (Git tag driven, prefix `v`) |
| **Styling** | Cyberpunk/OLED dark theme, 4px grid, CSS clamp() |

---

## 3. Project Structure (Onion Architecture)

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
│           │                             Archive, LocalModelLab, Login, Authentication
│           ├── Components/
│           ├── Services/               ← DuelApiClient, GuestAuthService, WebLlmService,
│           │                             SignalRDuelClient, AudioService, InferenceSessionStore
│           └── Layout/
├── tests/
│   ├── unit/PoLocalCompare.Unit.Tests/
│   ├── integration/PoLocalCompare.Integration.Tests/
│   └── e2e/PoLocalCompare.E2E/         ← Playwright TypeScript
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

### 4.4 Authentication
- **Dev/Test:** GUEST mode (click login → `GUEST{8 random digits}`, persisted to LocalStorage) + Microsoft OAuth
- **Prod:** Microsoft OAuth required; GUEST button is programmatically hidden/disabled
- Visual cue: `GUEST12345678 LOGGED IN` shown in nav bar

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

> Full standards are in [docs/PRD.md](docs/PRD.md) and [architecture.md](architecture.md).

- **No AOT.** `RunAOTCompilation=false` — keeps dev builds fast.
- **Warnings are errors.** `TreatWarningsAsErrors=true` globally.
- **Nullable enabled.** No null reference warnings tolerated.
- **SOLID + GoF.** Use `<remarks>` XML tags to explain pattern choices.
- **Zero-waste C#.** Delete unused files immediately. Optimize for human readability and LLM context.
- **Diagnostics endpoints:** `/health` (JSON, automated pings) and `/diag` (dev+prod hidden page). **No UI links to these.**
- **Mock visibility:** If `IMockable` services are active, display `USING MOCK DATA` in top nav.
- **Audio:** Web Audio API for in-app sounds.

---

## 8. Testing Strategy

| Test Type | Project | Notes |
|---|---|---|
| Unit | `PoLocalCompare.Unit.Tests` | Uses mock data (no real AI) |
| Integration | `PoLocalCompare.Integration.Tests` | Testcontainers (Azurite), test env |
| E2E | `PoLocalCompare.E2E` (Playwright TS) | GUEST auth, test env |

- Integration and E2E tests run against the **test** environment with non-AI mock data.
- Run the app locally against **dev** environment for real AI data.

---

## 9. Deployment (Azure)

```bash
# Validate and preview
azd provision --preview

# Deploy
azd up
```

- **Resource group:** `Punkouter26`
- **App Service Plan:** Check `PoShared` resource group if deployment fails
- **Key Vault:** `kv-poshared` (shared across Po* projects)
- **Managed Identity:** System-assigned, RBAC to storage and Key Vault

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
| [GuestAuthService.cs](src/Client/PoLocalCompare.Client/Services/GuestAuthService.cs) | GUEST auth (LocalStorage) |
| [WebLlmService.cs](src/Client/PoLocalCompare.Client/Services/WebLlmService.cs) | WebGPU browser inference |
| [architecture.md](architecture.md) | Full architecture record |
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
