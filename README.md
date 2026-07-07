# PoLocalCompare — LLM Duel Arena

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=.NET&logoColor=white)
![Blazor WASM](https://img.shields.io/badge/Blazor-WASM-512BD4?logo=Blazor&logoColor=white)
![Azure](https://img.shields.io/badge/Azure-Storage+Foundry-0089D6?logo=Microsoft+Azure&logoColor=white)
![WebLLM](https://img.shields.io/badge/WebLLM-WebGPU-FF6B35?logo=WebGPU&logoColor=white)

**PoLocalCompare** is a real-time benchmarking platform that pits **local browser-based LLMs** (via WebLLM/WebGPU) against **remote cloud models** (via Azure AI Foundry) in timed HTML-generation duels. An Elo rating system (K=32) tracks relative performance across all duels. The judge is always human.

> **Live at:** [polocalcompare.azurewebsites.net](https://polocalcompare.azurewebsites.net)

---

## 🚀 Quick Start

```bash
# 1. Start Azurite (Azure Storage emulator)
docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite

# 2. Download browser model assets locally (recommended on every new PC)
python SCRIPTS/download-models.py

# 3. Configure Azure AI Foundry (optional — enables remote model duels)
dotnet user-secrets set "AzureAiFoundry:ApiKey" "your-key"

# 4. Run the API
dotnet run --project src/PoLocalCompare.Api --launch-profile https

# 5. Open the app
open https://localhost:5001
```

If `python SCRIPTS/download-models.py` fails on a new machine, install the dependency first:

```bash
pip install huggingface_hub
```

---

## 🎯 What Is This?

| Capability | Description |
|---|---|
| **Local vs Remote** | Compare browser-based LLMs (WebLLM/WebGPU) against Azure AI Foundry models side-by-side |
| **Live Processing** | Real-time streaming of token generation with velocity, GPU stats, and HTML preview |
| **Elo Rankings** | K=32 ELO system tracks model performance across all duels |
| **Green Score** | Energy efficiency metric (tokens/Wh) for local models running on GPU |
| **Human Judge** | Side-by-side sandboxed HTML viewports — you pick the winner |
| **Auto-Judge** | GPT-4.1 Nano decides when users don't judge within the timeout window |
| **Lab Reports** | Export self-contained HTML reports for any duel |
| **Re-Challenge** | Re-run past prompts with different model pairs from the Archive |

---

## 📂 Project Structure

```
PoLocalCompare.slnx
├─ src/
│  ├─ PoLocalCompare.Api/             # Minimal API + SignalR + Blazor host — VSA slices
│  │   ├─ Features/                   # Duels, Leaderboard, Models, Archive, Ollama, Lobby, Diagnostics
│  │   │   └─ (endpoint + handlers + entities + repository per feature, flat)
│  │   ├─ Common/                     # Domain calculators, Inference proxies, KeyVault, Background, Telemetry
│  │   └─ Auth/                       # BFF cookie session + Microsoft OIDC + dev fake scheme
│  ├─ PoLocalCompare.Shared/          # DTOs and Enums shared with WASM client
│  │   └─ DTOs/, Enums/
│  └─ Client/PoLocalCompare.Client/   # Blazor WASM — 5 pages + Web Worker
│      └─ Pages/: WarRoom, Processing, Arena, Leaderboard, Archive, LocalModelLab
├─ tests/
│  ├─ PoLocalCompare.UnitTests/       # xUnit + Moq (pure logic)
│  ├─ PoLocalCompare.IntegrationTests/# WebApplicationFactory + Testcontainers.Azurite
│  ├─ PoLocalCompare.E2EAPI/          # Full client-server journeys over HTTP
│  └─ PoLocalCompare.E2EUI/           # C# Playwright UI tests
├─ infra/main.bicep                   # Azure resource definitions
└─ docs/                              # Consolidated documentation (this folder)
```

---

## 🧩 Key Concepts

### Duel Lifecycle

```
User (War Room)
    │
    ├─ Selects two models + enters prompt
    ├─ POST /api/duels → 202 Accepted (returns duelId)
    └─ Subscribes to SignalR group duel:{duelId}

Server (DuelExecutionService)
    │
    ├─ Local models: SignalR → client → WebLLM Web Worker (WebGPU)
    └─ Remote models: FoundryInferenceProxy → Azure AI Foundry

SignalR
    │
    └─ Streams ModelStatusUpdate events (token count, velocity, HTML preview)

User (Arena)
    │
    ├─ Reviews dual sandboxed HTML viewports
    ├─ Picks winner → POST /api/duels/{id}/verdict
    └─ EloCalculator updates ratings → Leaderboard reflects changes
```

### Model Types

| Type | Execution | Examples |
|---|---|---|
| **Local** | WebLLM in browser via WebGPU | Qwen2.5-3B, Llama3.2-1B, Phi-3.5-mini |
| **Remote** | Azure AI Foundry API | GPT-4o, GPT-4o-mini, o3-mini |
| **LocalService** | Ollama service (server-side) | gemma4, qwen3.5 (via `/api/ollama`) |

### Elo Rating System

- **Starting rating:** 1200
- **K-factor:** 32 (standard chess)
- **Formula:** `R'_a = Ra + K*(Sa - Ea)` where `Ea = 1/(1+10^((Rb-Ra)/400))`
- **Zero-sum:** winner/loser shifts always balance
- **Clamped expected probability:** 0.001–0.999 to prevent division issues

### Green Score (Local Models)

```
GreenScore = TokenCount / EnergyWh
EnergyWh = TdpWatts * (TotalDurationMs / 3,600,000)
EnergyCostUsd = EnergyWh * ElectricityRateUsd
```

---

## 🔧 Configuration

### appsettings.json keys

| Key | Description | Default |
|---|---|---|
| `AzureAiFoundry:ApiKey` | Azure AI Foundry API key | — (required for remote models) |
| `AzureAiFoundry:Endpoint` | Foundry endpoint URL | `https://your-resource.services.ai.azure.com` |
| `AzureAiFoundry:ModelName` | Default model name | `gpt-4o` |
| `ConnectionStrings:AzureTableStorage` | Table Storage connection string | `UseDevelopmentStorage=true` |
| `ConnectionStrings:AzureBlobStorage` | Blob Storage connection string | `UseDevelopmentStorage=true` |
| `Features:UseRealAi` | Enable/disable AI features in dev | `true` |
| `GreenStats:ElectricityRateUsd` | Electricity cost per kWh | `0.12` |
| `VerdictDeadlineHours` | Hours before auto-judge triggers | `24` |
| `Ollama:BaseUrl` | Ollama service URL | `http://localhost:11434` |
| `BrowserModels:CdnBaseUrlTemplate` | WebLLM model CDN template | — |

---

## 🧪 Testing

| Layer | Command | Location |
|---|---|---|
| **Unit** | `dotnet test tests/PoLocalCompare.UnitTests` | `tests/PoLocalCompare.UnitTests/` |
| **Integration** | `dotnet test tests/PoLocalCompare.IntegrationTests` | `tests/PoLocalCompare.IntegrationTests/` |
| **E2E API** | `dotnet test tests/PoLocalCompare.E2EAPI` | `tests/PoLocalCompare.E2EAPI/` |
| **E2E UI** | `dotnet test tests/PoLocalCompare.E2EUI` | `tests/PoLocalCompare.E2EUI/` |

### Key Test Coverage

- `EloCalculatorTests.cs` — Pure formula verification
- `RecordVerdictTests.cs` — Use-case with mocked repositories
- `DuelsEndpointTests.cs` — Full POST→verdict→leaderboard flow
- `LeaderboardTests.cs` — ELO ranking + Kill List validation

---

## 📊 Architecture

See [`docs/Architecture_MASTER.mmd`](docs/Architecture_MASTER.mmd) for the hybrid C4 Level 1/2 diagram.

### Vertical Slice Architecture (VSA)

```
PoLocalCompare.Api/Features/<Feature>/   ← endpoint + handlers + entities + repository, flat per feature
PoLocalCompare.Api/Common/               ← cross-slice domain services, inference proxies, host plumbing
```

- Each **feature slice** owns its endpoint, command/query handlers, entities, and Table Storage repository
- **Common/** holds only genuinely cross-slice code (Elo/green-score calculators, Foundry/Ollama proxies, Key Vault, background queue)
- **Api** hosts HTTP + SignalR + background tasks and serves the WASM client from the same origin
- See ADR 0002 (`docs/adr/`) for the migration record

### Azure Table Storage Schema

| Table | PartitionKey | RowKey | Purpose |
|---|---|---|---|
| `Models` | `model` | `{modelId}` (ULID) | Model registry |
| `Duels` | `YYYYMM` | `{duelId}` (ULID) | Duel sessions |
| `DuelResults` | `{duelId}` | `{modelId}` | Per-model telemetry |
| `EloHistory` | `{modelId}` | `{invertedTicks}_{duelId}` | ELO snapshots (sparklines) |

---

## 🎨 UI Design

- **OLED Black Theme:** `#000000` background, `#22c55e` green accent — optimized for AMOLED displays
- **Radzen Blazor Components:** DataGrid, TextArea, Button, Dialog
- **Sandboxed Viewports:** `<iframe sandbox="allow-scripts">` for model HTML output
- **Live Previews:** Real-time HTML rendering during token generation

---

## 🔐 Security

| Feature | Implementation |
|---|---|
| **CSP** | `frame-ancestors 'self'` — prevents clickjacking |
| **Key Vault** | Azure Key Vault for secrets (API keys, connection strings) |
| **Sandbox** | `<iframe sandbox="allow-scripts">` — prevents escape from rendered viewport |
| **CORS** | Configured for localhost dev origins only |
| **Error Surface** | RFC 7807 `application/problem+json` with correlationId |

---

## 🚢 Deployment

Infrastructure is defined in [`infra/main.bicep`](infra/main.bicep):

```bash
# Deploy to Azure
azd up

# Or manual Bicep deployment
az deployment group create \
  --resource-group PoLocalCompare-dev-rg \
  --template-file infra/main.bicep \
  --parameters environmentName=dev
```

### Azure Resources Created

- **App Service** (Linux, shared PoShared plan)
- **Storage Account** (Table Storage + Blob Storage)
- **RBAC** (Storage Table/Blob Data Contributor to App Service)

---

## 📖 Further Reading

| Document | Description |
|---|---|
| [`docs/PRD.md`](docs/PRD.md) | Complete Product Requirements Document |
| [`docs/Architecture_MASTER.mmd`](docs/Architecture_MASTER.mmd) | C4 Level 1/2 hybrid architecture |
| [`docs/ReleasePipeline_MASTER.mmd`](docs/ReleasePipeline_MASTER.mmd) | CI/CD pipeline strategy |
| [`docs/OnboardingJourney.mmd`](docs/OnboardingJourney.mmd) | New user flow |
| [`docs/PrimaryValueFlow.mmd`](docs/PrimaryValueFlow.mmd) | Critical path flowchart |
| [`docs/ExceptionUserFlows.mmd`](docs/ExceptionUserFlows.mmd) | Error/edge case flows |
| [`docs/SystemFlow_MASTER.mmd`](docs/SystemFlow_MASTER.mmd) | Consolidated system view |
| [`docs/StateDynamics_MASTER.mmd`](docs/StateDynamics_MASTER.mmd) | Entity state machines |
| [`docs/DataModel.mmd`](docs/DataModel.mmd) | ERD with schema |
| [`docs/AccessControl_MATRIX.mmd`](docs/AccessControl_MATRIX.mmd) | Role access matrix |
| [`docs/DataLifecycle_MASTER.mmd`](docs/DataLifecycle_MASTER.mmd) | Data flow pipeline |
| [`docs/SystemInteractionFlow.mmd`](docs/SystemInteractionFlow.mmd) | Component interaction sequence |
| [`docs/ServiceMap_MASTER.mmd`](docs/ServiceMap_MASTER.mmd) | Dependency graph |
| [`docs/InterfaceHierarchy_MASTER.mmd`](docs/InterfaceHierarchy_MASTER.mmd) | UI component tree |

---

## 📝 License

MIT

---

*Built with ASP.NET Core, Blazor WASM, Azure Table Storage, and WebLLM*