# Quickstart: PoLocalCompare — LLM Duel Arena

**Date**: 2026-05-09
**Audience**: Developer onboarding & local run validation

---

## Prerequisites

| Tool | Version | Purpose |
|------|---------|---------|
| .NET SDK | 10.x (pinned via `global.json`) | Backend + Blazor WASM build |
| Docker Desktop | Any current | Azurite (Table Storage emulator) |
| Node.js | 20 LTS | Playwright E2E tests |
| Edge / Chrome | 113+ | WebGPU-capable browser for local model inference |
| Azure CLI | Any current | Key Vault local auth (dev uses DefaultAzureCredential) |

---

## 1. Clone & Restore

```bash
git clone <repo-url>
cd PoLocalCompare
dotnet restore
```

---

## 2. Start Azurite (Docker)

```bash
docker run -d \
  --name azurite \
  -p 10000:10000 \
  mcr.microsoft.com/azure-storage/azurite \
  azurite-table --tableHost 0.0.0.0
```

Verify: `http://127.0.0.1:10000/devstoreaccount1/Tables` should return a 200/empty list.

> **Constitution § VII**: Do NOT use local storage emulators other than Azurite in Docker.

---

## 3. Configure appsettings.Development.json

The file `src/PoLocalCompare.Api/appsettings.Development.json` is `.gitignore`d. Create it with:

```json
{
  "ConnectionStrings": {
    "AzureTableStorage": "UseDevelopmentStorage=true"
  },
  "AzureAiFoundry": {
    "Endpoint": "https://<your-foundry-endpoint>.cognitiveservices.azure.com/",
    "DeploymentName": "<your-deployment-name>"
  },
  "GreenStats": {
    "DefaultTdpWatts": 115.0,
    "ElectricityRateUsd": 0.15
  },
  "Elo": {
    "KFactor": 32,
    "StartingRating": 1200
  },
  "Duel": {
    "TimeLimitSeconds": 300
  },
  "Features": {
    "UseRealAi": true
  }
}
```

> **No secrets in appsettings** (Constitution § V). Azure AI Foundry keys come from Key Vault via DefaultAzureCredential in all environments. For local dev, run `az login` once to authenticate.

---

## 4. Run the Application (F5 / CLI)

```bash
# Kills any existing dotnet processes, then starts server
cd src/PoLocalCompare.Api
dotnet run --launch-profile "https"
```

Or press **F5** in VS Code — the launch task kills existing dotnet processes and opens `https://localhost:5001` in Edge automatically.

---

## 5. Verify Everything Works

### Health check
```bash
curl -k https://localhost:5001/health
```
Expected: `{"status":"Healthy",...}` with all checks green.

### Diagnostics page
Open `https://localhost:5001/diag` in browser. Verify:
- Azure Table Storage: Healthy
- Azure AI Foundry: Healthy (or configured)
- WebGPU: Available (shown on client once page loads)

### Scalar OpenAPI UI
Open `https://localhost:5001/scalar` — all endpoints listed and callable.

---

## 6. Run a Test Duel (via .http file)

Open `src/PoLocalCompare.Api/requests/duels.http` in VS Code REST Client:

```http
### List models
GET https://localhost:5001/api/models

### Start a duel
POST https://localhost:5001/api/duels
Content-Type: application/json

{
  "leftModelId": "{{leftModelId}}",
  "rightModelId": "{{rightModelId}}",
  "promptText": "Build a single-file HTML stopwatch with start, stop, and reset buttons."
}
```

---

## 7. Run Tests

```bash
# Unit tests
dotnet test tests/unit/PoLocalCompare.Unit.Tests

# Integration tests (requires Docker / Azurite)
dotnet test tests/integration/PoLocalCompare.Integration.Tests

# E2E tests (headed — requires Edge/Chrome + app running)
cd tests/e2e/PoLocalCompare.E2E
npx playwright test --headed
```

> **Mock data in tests**: Integration and E2E tests use mock AI responses (`Features:UseRealAi = false`). A **MOCK DATA** banner appears at the top of affected pages.

---

## 8. Validation Checklist

- [X] `https://localhost:5001` loads the War Room (OLED Black theme, dual model columns)
- [X] `https://localhost:5001/health` returns `{"status":"Healthy"}`
- [X] `https://localhost:5001/diag` shows all connections green
- [X] `https://localhost:5001/scalar` shows all API endpoints
- [X] Selecting a local + remote model enables the Commence Duel button
- [X] Pressing Commence Duel plays the snare-roll audio cue
- [X] Processing-phase HUD shows two per-model status panels with elapsed time
- [X] Arena reveals two sandboxed viewports after duel completes
- [X] Clicking Winner updates ELO and plays success audio
- [X] Leaderboard reflects updated ratings within 3 seconds
- [X] Lab Archive shows the completed duel
- [X] Lab Report export downloads a single self-contained HTML file
