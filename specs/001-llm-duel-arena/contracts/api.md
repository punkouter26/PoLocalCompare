# API Contracts: PoLocalCompare — LLM Duel Arena

**Phase**: 1 — Design
**Date**: 2026-05-09
**Format**: REST + SignalR Hub

Base URL (local dev): `https://localhost:5001`
OpenAPI (Scalar) UI: `https://localhost:5001/scalar`
Health endpoint: `https://localhost:5001/health`
Diagnostics page: `https://localhost:5001/diag`

---

## REST Endpoints

### Models

#### `GET /api/models`
Returns all registered models with current ELO and metadata.

**Response 200**:
```json
[
  {
    "modelId": "01J...",
    "displayName": "Gemma 4 (Local)",
    "modelType": "Local",
    "currentElo": 1247.5,
    "duelCount": 12,
    "winCount": 7,
    "greenScoreAvg": 142.3,
    "webLlmModelId": "gemma-4-it-q4f32_1",
    "tdpWatts": 115.0
  },
  {
    "modelId": "01K...",
    "displayName": "GPT-4o (Azure Foundry)",
    "modelType": "Remote",
    "currentElo": 1352.0,
    "duelCount": 12,
    "winCount": 5,
    "greenScoreAvg": null,
    "apiEndpointRef": "gpt-4o-deployment"
  }
]
```

#### `POST /api/models`
Register a new model in the registry.

**Request body**:
```json
{
  "displayName": "Phi-4 (Local)",
  "modelType": "Local",
  "webLlmModelId": "phi-4-q4f32_1",
  "tdpWatts": 115.0
}
```

**Response 201**: created model object (same shape as GET item).
**Response 422**: validation errors.

---

### Duels

#### `POST /api/duels`
Commence a new duel. Server creates the Duel record, stores it as Pending, and returns the duelId so the client can subscribe to the SignalR hub.

**Request body**:
```json
{
  "leftModelId": "01J...",
  "rightModelId": "01K...",
  "promptText": "Build a single-file HTML Pomodoro timer with start/stop and a circular progress ring."
}
```

**Response 202 Accepted**:
```json
{
  "duelId": "01M...",
  "promptFull": "Build a single-file HTML Pomodoro timer... [Use public CDNs for any external libraries — do not reference local files.]",
  "leftModelId": "01J...",
  "rightModelId": "01K...",
  "startedAt": "2026-05-09T14:00:00Z",
  "timeLimitSeconds": 300
}
```

**Response 422**: if same model selected twice, or modelIds do not exist.

#### `GET /api/duels`
List all duels (reverse chronological). Supports query params:
- `?limit=20` (default 20, max 100)
- `?before=YYYYMM` (month partition cursor for pagination)

**Response 200**: array of duel summary objects (no HTML output):
```json
[
  {
    "duelId": "01M...",
    "promptSummary": "Build a single-file HTML Pomodoro timer...",
    "leftModelId": "01J...",
    "leftModelName": "Gemma 4 (Local)",
    "rightModelId": "01K...",
    "rightModelName": "GPT-4o (Azure Foundry)",
    "startedAt": "2026-05-09T14:00:00Z",
    "completedAt": "2026-05-09T14:03:21Z",
    "verdict": "Left",
    "winnerModelId": "01J..."
  }
]
```

#### `GET /api/duels/{duelId}`
Full duel detail including both DuelResults (HTML output included).

**Response 200**:
```json
{
  "duelId": "01M...",
  "promptText": "Build a single-file HTML Pomodoro timer...",
  "promptFull": "Build a single-file HTML Pomodoro timer... [CDN suffix]",
  "startedAt": "2026-05-09T14:00:00Z",
  "completedAt": "2026-05-09T14:03:21Z",
  "verdict": "Left",
  "winnerModelId": "01J...",
  "eloShiftWinner": 12.3,
  "eloShiftLoser": -12.3,
  "results": [
    {
      "modelId": "01J...",
      "modelName": "Gemma 4 (Local)",
      "warmUpDurationMs": 4200,
      "generationDurationMs": 181000,
      "totalDurationMs": 185200,
      "tokenCount": 1843,
      "tokenVelocity": 10.18,
      "htmlOutputRaw": "<!DOCTYPE html>...",
      "htmlOutputSizeBytes": 14832,
      "characterDensityRatio": 0.74,
      "isFailure": false,
      "failureReason": null,
      "energyWh": 5.93,
      "energyCostUsd": 0.00089,
      "apiCostUsd": null,
      "greenScore": 310.8
    },
    {
      "modelId": "01K...",
      "modelName": "GPT-4o (Azure Foundry)",
      "warmUpDurationMs": 0,
      "generationDurationMs": 9400,
      "totalDurationMs": 9400,
      "tokenCount": 1920,
      "tokenVelocity": 204.3,
      "htmlOutputRaw": "<!DOCTYPE html>...",
      "htmlOutputSizeBytes": 15100,
      "characterDensityRatio": 0.76,
      "isFailure": false,
      "failureReason": null,
      "energyWh": null,
      "energyCostUsd": null,
      "apiCostUsd": 0.0038,
      "greenScore": null
    }
  ]
}
```

#### `POST /api/duels/{duelId}/verdict`
Record the user's winner selection and trigger ELO calculation.

**Request body**:
```json
{ "verdict": "Left" }
```

**Response 200**:
```json
{
  "duelId": "01M...",
  "verdict": "Left",
  "winnerModelId": "01J...",
  "loserModelId": "01K...",
  "eloShiftWinner": 12.3,
  "eloShiftLoser": -12.3,
  "winnerEloAfter": 1259.8,
  "loserEloAfter": 1339.7
}
```

**Response 409 Conflict**: if verdict already recorded for this duel.
**Response 422**: invalid verdict value.

#### `GET /api/duels/{duelId}/report`
Download the self-contained HTML Lab Report.

**Response 200**:
- `Content-Type: text/html`
- `Content-Disposition: attachment; filename="lab-report-{duelId}.html"`
- Body: single self-contained HTML file with inlined CSS, telemetry, ELO data, and both model source outputs.

---

### Leaderboard

#### `GET /api/leaderboard`
All models ranked by ELO descending. Supports `?sortBy=elo` (default) or `?sortBy=greenScore`.

**Response 200**:
```json
[
  {
    "rank": 1,
    "modelId": "01K...",
    "displayName": "GPT-4o (Azure Foundry)",
    "currentElo": 1352.0,
    "duelCount": 12,
    "winCount": 5,
    "greenScoreAvg": null,
    "eloSparkline": [1200, 1215, 1231, 1208, 1224, 1240, 1255, 1239, 1252, 1268, 1280, 1295, 1310, 1325, 1339, 1328, 1340, 1345, 1350, 1352]
  }
]
```

Note: `eloSparkline` contains up to 20 values (oldest→newest), one per duel.

#### `GET /api/leaderboard/{modelId}/killlist`
Head-to-head record for a given model against all opponents.

**Response 200**:
```json
[
  {
    "opponentModelId": "01J...",
    "opponentName": "Gemma 4 (Local)",
    "wins": 3,
    "losses": 7,
    "lastDuelId": "01M..."
  }
]
```

---

### Health & Diagnostics

#### `GET /health`
Returns JSON health status (no authentication required).

**Response 200**:
```json
{
  "status": "Healthy",
  "checks": {
    "azureTableStorage": { "status": "Healthy", "latencyMs": 12 },
    "azureAiFoundry": { "status": "Healthy", "latencyMs": 84 },
    "keyVault": { "status": "Healthy", "latencyMs": 23 }
  }
}
```

**Response 503**: if any check is `Unhealthy`.

#### `GET /diag`
Blazor page (server-rendered). Displays:
- Azure Table Storage connection status + latency
- Azure AI Foundry endpoint status + configured deployment name
- Azure Key Vault status
- Configuration keys in use (sensitive values masked: first 4 + `****` + last 4 chars)
- WebGPU availability (reported from client via JS interop)
- Current `appsettings` feature flag states

---

## SignalR Hub

### `/hubs/duel`

#### Client → Server

**`JoinDuel(duelId: string)`**
Subscribe to progress events for a specific duel.

#### Server → Client

**`ModelStatusUpdate`**
Fired every 500ms during processing.

```json
{
  "duelId": "01M...",
  "modelId": "01J...",
  "side": "Left",
  "status": "Generating",
  "elapsedMs": 12400,
  "tokenCount": 312,
  "estimatedRemainingMs": 167600
}
```

`status` values: `"Initializing"` | `"Generating"` | `"Done"` | `"Failed"`

**`DuelComplete`**
Fired when both models reach `Done` or `Failed` status.

```json
{
  "duelId": "01M...",
  "completedAt": "2026-05-09T14:03:21Z"
}
```

Upon receiving `DuelComplete`, the client navigates to the Arena page and fetches `GET /api/duels/{duelId}` to load full results.

---

## Security Notes

- All API endpoints except `/health` and `/diag` require the server to be running on localhost (no cross-origin calls from untrusted origins; CORS is restricted to `localhost:5000` / `localhost:5001`).
- Model-generated HTML in `htmlOutputRaw` is sanitised server-side before being embedded in Lab Reports (XSS prevention per research.md §7).
- Arena viewports render HTML via `<iframe srcdoc>` with restricted `sandbox` attribute (no `allow-same-origin` — prevents parent DOM access).
- API keys for Azure AI Foundry are held only in Key Vault; the `/api/duels` POST flow proxies the prompt server-side and never returns credentials to the client.
