# PRD.md — PoLocalCompare LLM Duel Arena

## 1. Concept & Vision

**PoLocalCompare** is a real-time LLM benchmarking platform that pits local browser-based LLMs (WebLLM/WebGPU) against remote cloud models (Azure AI Foundry) in timed HTML-generation duels. The platform transforms abstract model capabilities into visceral, competitive experiences where users witness token-by-token generation, energy consumption, and output quality in real-time.

**The feel:** A cyberpunk command center meets chess ranking system. Dark, high-contrast OLED-optimized UI with neon green accents. Every duel feels like a showdown. Rankings feel consequential.

**The promise:** "See your local model compete against GPT-4o in real-time. Watch the tokens flow. Pick the winner. Let the ELO tell the truth."

---

## 2. Design Language

### Aesthetic Direction
Cyberpunk command center meets professional chess ranking system. Dark-first design with surgical precision. Not "dark mode" — this is OLED-optimized with pure black backgrounds and neon accents that feel like an arcade.

### Color Palette

| Role | Hex | Usage |
|---|---|---|
| **Background** | `#000000` | Pure black for OLED energy savings |
| **Surface** | `#0f0f0f` | Cards, panels, elevated surfaces |
| **Border** | `#1f1f1f` | Subtle dividers, input borders |
| **Text** | `#ffffff` | Primary text |
| **Text Muted** | `#6b7280` | Secondary text, labels |
| **Green** | `#22c55e` | Primary accent — wins, success, active states |
| **Cyan** | `#06b6d4` | Secondary accent — info, links, highlights |
| **Red** | `#ef4444` | Errors, failures, warnings |
| **Yellow** | `#eab308` | Pending, caution states |

### Typography

- **Primary:** Geist (sans-serif) — clean, modern, highly legible
- **Mono:** Geist Mono — technical content (ports, commands, URLs)
- **Display:** Instrument Serif — page titles only (optional)

### Spacing System
4px base grid. All dimensions divisible by 4.

### Motion Philosophy
- Token streaming: smooth, no jarring jumps
- Status transitions: 150ms ease-out
- Page navigation: immediate, no fade delays
- Real-time updates: 500ms polling interval for SignalR

---

## 3. Layout & Structure

### Page Architecture

```
/
├── War Room (/) — Home page, model selection, duel initiation
├── Processing (/processing/{duelId}) — Live duel monitoring
├── Arena (/arena/{duelId}) — Side-by-side HTML comparison, verdict
├── Leaderboard (/leaderboard) — ELO rankings, kill lists, sparklines
├── Archive (/archive) — Historical duels, re-challenge
└── Local Model Lab (/local-model-lab) — GPU diagnostics, model management
```

### Responsive Strategy
- **Desktop (≥1024px):** Full side-by-side layouts, multi-column grids
- **Mobile:** Single-column, scroll-snap for duel comparison
- **Breakpoints:** 640px (mobile), 768px (tablet), 1024px (desktop)

---

## 4. Features & Interactions

### 4.1 War Room — Model Selection & Duel Initiation

**Purpose:** Select two models, enter prompt, commence duel.

**Model Pool:**
- **Local (WebLLM):** Browser-based, WebGPU-accelerated. Requires first-run model download (~500MB–2GB).
- **Remote (Azure Foundry):** Cloud-hosted, instant availability.
- **LocalService (Ollama):** Server-side local inference via Ollama service.

**Model Card Display:**
- Display name, model type badge, current ELO
- TDP (watts) for local models
- API cost indicator for remote models
- Green Score (tokens/Wh) if available
- Availability status (confirmed working / not checked / unavailable)

**Interaction Flow:**
1. User clicks model → assigned to "Left" slot
2. User clicks second model → assigned to "Right" slot
3. User clicks same model again → deselects (toggle behavior)
4. Both slots filled → "Commence Duel" button enables
5. Prompt textarea required for button to enable

**Validation Rules:**
- LeftModel ≠ RightModel (must be different)
- Both models must be marked as "available"
- Prompt must not be empty
- User must wait for local model confirmation on first use

**Error States:**
- Model unavailable: "Not confirmed working right now" — card disabled
- Network failure: Red toast notification with retry option
- Storage not ready: "Duel storage is initializing. Please retry."

### 4.2 Processing — Live Duel Monitoring

**Purpose:** Real-time visualization of both models generating HTML.

**Panel Layout:**
- Left and Right side-by-side processing panels
- Each panel shows:
  - Model name + type badge
  - Status (Initializing → Generating → Done/Failed)
  - Elapsed time (ms)
  - Token count
  - Token velocity (tokens/sec)
  - Peak velocity
  - Warm-up duration
  - GPU badge (for Ollama)
  - Stall indicator (if no tokens for >10s)

**Advanced Metrics:**
- HTML tag count
- Open tag depth
- Style rule count
- Repetition score
- Prefill speed (TPS)
- Cache hit indicator

**Live HTML Preview:**
- Real-time partial HTML rendered in sandboxed iframe
- Updates every ~500ms as tokens arrive
- Shows model name label on each preview

**Navigation:**
- Page refresh: rehydrates state from API
- Duel already judged: auto-navigates to Arena
- Both sides complete: auto-navigates to Arena

**Error Handling:**
- Watchdog timeout (900s): marks model as Failed
- Worker crash: error toast, option to retry

### 4.3 Arena — Human Verdict

**Purpose:** Side-by-side HTML comparison, human winner selection.

**Layout:**
- Full-width dual viewport (iframe sandbox="allow-scripts")
- Left model on left, Right model on right
- Each viewport has model name label above
- "Winner" buttons below each viewport

**Interaction:**
- User reviews both outputs
- Clicks "Left Wins" or "Right Wins" button
- Confirmation dialog before recording verdict
- POST /api/duels/{id}/verdict with verdict

**States:**
- Pending verdict: buttons active
- Verdict recorded: buttons disabled, winner highlighted
- Expired (24h): auto-judge triggered, verdict locked

**Auto-Judge:**
- Triggered if no verdict within VerdictDeadlineHours
- Uses GPT-4.1 Nano via Azure AI Foundry
- Calls /api/duels/{id}/auto-judge endpoint

### 4.4 Leaderboard — Rankings & Kill Lists

**Purpose:** ELO rankings, model comparison, head-to-head history.

**Columns:**
- Rank (🥇🥈🥉 for top 3)
- Model Name (+ type badge: LOCAL/REMOTE/SVC)
- ELO (integer)
- Duels count
- W/L (win percentage)
- Output Quality (🧪, if available)
- Green Score (🌱, if available)
- ELO Trend sparkline (last 20 duels)

**Sorting:**
- By ELO (default)
- By Green Score (unlocks after local model duels)
- By Output Quality (unlocks after quality data available)

**Filters:**
- Show All / Active only (models with duels)

**Kill List:**
- Click any model → shows head-to-head vs all opponents
- Columns: Opponent, W/L, Win %, Last Duel
- Sorted by win rate (most favorable matchups first)

### 4.5 Archive — Historical Duels

**Purpose:** Browse past duels, export lab reports, re-challenge.

**Columns:**
- Date (yyyy-MM-dd HH:mm)
- Prompt summary
- Left model name
- Right model name
- Verdict (Left / Right / No Verdict)

**Filters:**
- All / Won (has verdict) / No Verdict (pending)

**Row Expansion:**
- Click row to expand details
- Shows: Prompt, models, started/completed times, winner
- Actions: Re-Challenge (navigates to War Room with pre-filled params), Judge This Duel (if pending)

**Pagination:**
- Load 20 at a time
- Scroll to load more

### 4.6 Local Model Lab — GPU Diagnostics

**Purpose:** Check Ollama GPU availability, manage local models, view WebLLM status.

**Ollama Status:**
- List of available Ollama models
- GPU acceleration indicator
- One-click test inference

**WebLLM Status:**
- Browser GPU compatibility check
- Model cache status
- Download progress for first-run models

---

## 5. Component Inventory

### ModelCard
- **Default:** Dark surface, border, model info
- **Hover:** Border brightens, subtle lift
- **Selected (Left):** Green border, "LEFT" label
- **Selected (Right):** Green border, "RIGHT" label
- **Disabled:** Opacity 0.5, cursor not-allowed, unavailable reason text

### ProcessingPanel
- **Initializing:** Pulsing animation, "Initializing..." text
- **Generating:** Live token counter, velocity graph, HTML preview
- **Done:** Green checkmark, final token count, completion time
- **Failed:** Red X, failure reason, retry option
- **Stalled:** Yellow warning, "Stalled" indicator

### VerdictButton
- **Default:** Surface background, white text
- **Hover:** Green background, black text
- **Active (clicked):** Loading spinner
- **Disabled (after verdict):** Opacity 0.5

### EloSparkline
- SVG line chart
- Green line on dark background
- 20 data points max
- Auto-scaling Y axis

### SandboxedViewport
- iframe with sandbox="allow-scripts"
- Model name label above
- Border matching model type color
- Responsive height (min 300px)

---

## 6. Technical Approach

### Architecture: Onion / Clean Architecture

```
┌─────────────────────────────────────────────────────────┐
│  Client (Blazor WASM)                                    │
│  Pages: WarRoom, Processing, Arena, Leaderboard, Archive │
│  Services: DuelApiClient, SignalRDuelClient, WebLlmService │
└───────────────────────┬─────────────────────────────────┘
                        │ HTTP + SignalR (WSS)
┌───────────────────────▼─────────────────────────────────┐
│  PoLocalCompare.Api (ASP.NET Core)                       │
│  Minimal API Endpoints + SignalR DuelHub + Scalar        │
├─────────────────────────────────────────────────────────┤
│  PoLocalCompare.Application                              │
│  Use-case handlers (CQRS pattern)                        │
│  Interfaces: IDuelRepository, IModelRepository, etc.      │
├─────────────────────────────────────────────────────────┤
│  PoLocalCompare.Infrastructure                            │
│  Azure Table Storage implementation                      │
│  Azure AI Foundry proxy                                  │
├─────────────────────────────────────────────────────────┤
│  PoLocalCompare.Domain                                   │
│  Entities, Value Objects, Domain Services                │
│  EloCalculator (pure, static)                           │
├─────────────────────────────────────────────────────────┤
│  PoLocalCompare.Shared                                   │
│  DTOs and Enums shared between server and client          │
└─────────────────────────────────────────────────────────┘
```

### Data Model

#### Entities

**Model**
```csharp
public sealed class Model
{
    public string ModelId { get; init; }
    public string DisplayName { get; set; }
    public ModelType ModelType { get; init; }
    public double CurrentElo { get; set; }
    public int DuelCount { get; set; }
    public int WinCount { get; set; }
    public double GreenScoreAvg { get; set; }
    
    // Local models only
    public double? TdpWatts { get; init; }
    public string? WebLlmModelId { get; init; }
    
    // Remote models only
    public string? ApiEndpointRef { get; init; }
    public decimal? InputTokenPricePerMillion { get; init; }
    public decimal? OutputTokenPricePerMillion { get; init; }
    
    public DateTimeOffset CreatedAt { get; init; }
}
```

**Duel**
```csharp
public sealed class Duel
{
    public string DuelId { get; init; }
    public string PromptText { get; init; }
    public string PromptFull { get; init; }
    public string LeftModelId { get; init; }
    public string RightModelId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DuelVerdict Verdict { get; set; }
    public string? WinnerModelId { get; set; }
    public string? LoserModelId { get; set; }
    public double? EloShiftWinner { get; set; }
    public double? EloShiftLoser { get; set; }
    public DateTimeOffset VerdictDeadline { get; init; }
    public bool IsPartial { get; set; }
}
```

**DuelResult**
```csharp
public sealed class DuelResult
{
    public string DuelId { get; init; }
    public string ModelId { get; init; }
    public long WarmUpDurationMs { get; set; }
    public long GenerationDurationMs { get; set; }
    public long TotalDurationMs { get; set; }
    public int TokenCount { get; set; }
    public double TokenVelocity { get; set; }
    public string HtmlOutputRaw { get; set; }
    public long HtmlOutputSizeBytes { get; set; }
    public double CharacterDensityRatio { get; set; }
    public int OutputQualityScore { get; set; }
    public bool IsFailure { get; set; }
    public string? FailureReason { get; set; }
    
    // Local models only
    public double? EnergyWh { get; set; }
    public double? EnergyCostUsd { get; set; }
    public double? GreenScore { get; set; }
    
    // Remote models only
    public double? ApiCostUsd { get; set; }
}
```

**EloRecord**
```csharp
public sealed class EloRecord
{
    public string ModelId { get; init; }
    public string TimestampKey { get; init; } // inverted ticks for desc order
    public string DuelId { get; init; }
    public double EloAfter { get; init; }
    public double EloBefore { get; init; }
    public double EloShift { get; init; }
    public string Outcome { get; init; }
    public string OpponentModelId { get; init; }
    public double OpponentEloBefore { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}
```

#### Enums

**ModelType**
```csharp
public enum ModelType { Local, Remote, LocalService }
```

**DuelStatus**
```csharp
public enum DuelStatus { Initializing, Generating, Done, Failed }
```

**DuelVerdict**
```csharp
public enum DuelVerdict { Pending, Left, Right, Expired }
```

### Azure Table Storage Schema

| Table | PartitionKey | RowKey | Purpose |
|---|---|---|---|
| `Models` | `"model"` | `{modelId}` (ULID) | Model registry |
| `Duels` | `{YYYYMM}` | `{duelId}` (ULID) | Duel sessions (time-partitioned) |
| `DuelResults` | `{duelId}` | `{modelId}` | Per-model telemetry |
| `EloHistory` | `{modelId}` | `{invertedTicks}_{duelId}` | ELO snapshots for sparklines |

### API Endpoints

| Method | Path | Description |
|---|---|---|
| POST | `/api/duels` | Commence new duel |
| GET | `/api/duels` | List duels (archive) |
| GET | `/api/duels/{duelId}` | Get duel with results |
| POST | `/api/duels/{duelId}/local-result` | Post local WebLLM result |
| POST | `/api/duels/{duelId}/verdict` | Record human verdict |
| POST | `/api/duels/{duelId}/auto-judge` | Auto-judge with GPT-4.1 Nano |
| GET | `/api/duels/{duelId}/report` | Export lab report HTML |
| GET | `/api/leaderboard` | Get ELO leaderboard |
| GET | `/api/leaderboard/{modelId}/killlist` | Get head-to-head history |
| GET | `/api/models` | List all models |
| POST | `/api/models` | Add new model |
| PUT | `/api/models/{modelId}` | Update model |
| DELETE | `/api/models/{modelId}` | Delete model |
| GET | `/api/ollama/status` | Get Ollama GPU status |
| GET | `/api/ollama/models` | List Ollama models |
| GET | `/health` | Health check |
| POST | `/api/dev/reset` | Dev-only: reset duels/ELO |

### SignalR Hub

**Hub:** `/hubs/duel`

**Methods (Client → Server):**
- `JoinDuel(string duelId)` — Subscribe to duel broadcast group

**Methods (Server → Client):**
- `ModelStatusUpdate(ModelStatusUpdateDto)` — Streaming update
- `StartLocalInference(StartLocalInferencePayload)` — Trigger WebLLM
- `DuelComplete(DuelDto)` — Both sides finished

### Duel Execution Flow

```
1. POST /api/duels → 202 Accepted
   └─ CommenceDuelHandler creates Duel entity
   └─ DuelExecutionService.EnqueueAsync() adds to BackgroundTaskQueue

2. BackgroundTaskService picks up task
   └─ ExecuteAsync():
       ├─ Load Duel + both Models
       ├─ Create 900s watchdog
       ├─ RunModelAsync(Left) + RunModelAsync(Right) concurrently
       │
       ├─ For Local models:
       │   └─ WaitForLocalModelResultAsync():
       │       ├─ SignalR: SendStartLocalInference
       │       └─ Poll DuelResultRepository until result posted
       │
       └─ For Remote models:
           └─ FoundryInferenceProxy.RunInferenceAsync():
               ├─ POST to Azure AI Foundry
               ├─ Stream response tokens
               └─ SendStatusAsync every ~500ms via SignalR

3. Both complete → DuelComplete event via SignalR
   └─ Clients auto-navigate to /arena/{duelId}

4. Arena: Human judges → POST /api/duels/{id}/verdict
   └─ RecordVerdictHandler:
       ├─ Load Duel + both Models
       ├─ EloCalculator.Calculate() → new ratings
       ├─ Update Model.CurrentElo, DuelCount, WinCount
       ├─ Save EloRecord for sparkline history
       └─ Return VerdictResponseDto
```

### Elo Calculation (Domain Service)

```csharp
public static class EloCalculator
{
    public static (double NewRatingA, double NewRatingB) Calculate(
        double ratingA,
        double ratingB,
        double k,
        double outcomeA)
    {
        var expectedA = Math.Clamp(1.0 / (1.0 + Math.Pow(10, (ratingB - ratingA) / 400.0)), 0.001, 0.999);
        var expectedB = 1.0 - expectedA;
        var outcomeB = 1.0 - outcomeA;

        var newRatingA = Math.Round(ratingA + k * (outcomeA - expectedA), 1);
        var newRatingB = Math.Round(ratingB + k * (outcomeB - expectedB), 1);

        // Minimum 0.1 shift for decisive outcomes
        if (outcomeA is 1.0 or 0.0)
        {
            if (Math.Abs(newRatingA - ratingA) < 0.1 && Math.Abs(newRatingA - ratingA) > 0)
                newRatingA = ratingA + (newRatingA >= ratingA ? 0.1 : -0.1);
            if (Math.Abs(newRatingB - ratingB) < 0.1 && Math.Abs(newRatingB - ratingB) > 0)
                newRatingB = ratingB + (newRatingB >= ratingB ? 0.1 : -0.1);
        }

        return (newRatingA, newRatingB);
    }
}
```

### Green Score Calculation

```csharp
public static class GreenStatsCalculator
{
    public static double ComputeEnergyWh(double tdpWatts, long totalDurationMs)
        => tdpWatts * (totalDurationMs / 3_600_000.0);

    public static double ComputeEnergyCostUsd(double energyWh, double rateUsd)
        => (energyWh / 1000.0) * rateUsd;

    public static double ComputeGreenScore(int tokenCount, double energyWh)
        => energyWh > 0 ? Math.Round(tokenCount / energyWh, 2) : 0;
}
```

### WebLLM Integration (Client)

**Web Worker:** `wwwroot/js/webllm-worker.js`

**Flow:**
1. Processing.razor calls `WebLlmService.StartInferenceAsync()`
2. WebLlmService invokes `window.startWebLlmInference()` on JS interop
3. JS starts Web Worker, loads model via WebLLM API
4. Worker streams tokens back via `postMessage`
5. Processing.razor receives updates via `WebLlmService.ReceiveStatusUpdate()`
6. On completion, `WebLlmService.ReceiveComplete()` provides HTML payload
7. Processing.razor POSTs result to `/api/duels/{id}/local-result`

### Azure AI Foundry Integration (Server)

**Proxy:** `FoundryInferenceProxy.cs`

**Flow:**
1. DuelExecutionService.RunModelAsync() calls `proxy.RunInferenceAsync()`
2. Proxy POSTs to `/chat/completions` with model config
3. Streams response via SSE
4. Extracts tokens, updates SignalR clients
5. On completion, returns DuelResult

### Observability

- **Serilog:** Console + File (logs/) + App Insights (production)
- **OpenTelemetry:** Traces + Metrics → App Insights
- **Health Check:** `/health` pings Table Storage + Foundry + Key Vault
- **Diag Page:** `/diag` shows connection statuses + config (dev only)

### Security

| Concern | Implementation |
|---|---|
| Secrets | Azure Key Vault (production), user-secrets (dev) |
| Storage Keys | Key Vault → App Settings via Key Vault references |
| CORS | Configured for localhost origins only |
| CSP | `frame-ancestors 'self'` — prevents clickjacking |
| Sandbox | `<iframe sandbox="allow-scripts">` — prevents DOM access |
| Error Format | RFC 7807 `application/problem+json` with correlationId |

### Deployment

**Infrastructure:** `infra/main.bicep`

**Resources:**
- App Service (Linux, shared ASP PoShared plan)
- Storage Account (Table Storage + Blob Storage)
- RBAC: Storage Table/Blob Data Contributor to App Service identity

**Deployment:** `azd up` or manual `az deployment group create`

---

## 7. Testing Strategy

### Unit Tests
- `EloCalculatorTests.cs` — Pure formula verification
- `GreenStatsCalculatorTests.cs` — Energy calculations
- `ModelEntityTests.cs` — Validation rules
- `DuelEntityTests.cs` — Business rules

### Integration Tests
- `DuelsEndpointTests.cs` — Full POST→verdict→leaderboard flow
- `LeaderboardTests.cs` — ELO ranking + Kill List
- `ModelRepositoryTests.cs` — Table storage CRUD
- `FoundryProxyTests.cs` — (mocked) API calls

### E2E Tests (Playwright)
- War Room: model selection, duel commencement
- Processing: live update streaming
- Arena: verdict recording, ELO update
- Leaderboard: sorting, filtering, kill list
- Archive: pagination, re-challenge

---

## 8. Configuration Reference

| Key | Type | Default | Description |
|---|---|---|---|
| `AzureAiFoundry:ApiKey` | string | — | Required for remote models |
| `AzureAiFoundry:Endpoint` | string | — | Foundry endpoint URL |
| `AzureAiFoundry:ModelName` | string | `gpt-4o` | Default model |
| `ConnectionStrings:AzureTableStorage` | string | `UseDevelopmentStorage=true` | Table storage connection |
| `ConnectionStrings:AzureBlobStorage` | string | `UseDevelopmentStorage=true` | Blob storage connection |
| `Features:UseRealAi` | bool | `true` | Enable AI features in dev |
| `GreenStats:ElectricityRateUsd` | double | `0.12` | Electricity cost per kWh |
| `VerdictDeadlineHours` | int | `24` | Hours before auto-judge |
| `Ollama:BaseUrl` | string | `http://localhost:11434` | Ollama service URL |
| `BrowserModels:CdnBaseUrlTemplate` | string | — | WebLLM CDN template |
| `KeyVault:Uri` | string | — | Key Vault URI (production) |

---

## 9. Non-Functional Requirements

### Performance
- SignalR updates: 500ms interval
- Page load: <3s on broadband
- Local model inference: varies by hardware
- Remote model inference: <30s typical

### Scalability
- Stateless API (Azure App Service)
- Table storage for session data (no affinity needed)
- SignalR for real-time (sticky not required)

### Availability
- Health endpoint: `/health`
- Storage resilience: Azure Table Storage SLA
- Fallback: poll-based completion check if SignalR missed

### Accessibility
- Keyboard navigation (N = focus prompt)
- ARIA labels on interactive elements
- Color contrast: WCAG AA minimum

---

## 10. Future Considerations

- **Model fine-tuning:** User uploads custom model weights
- **Multi-round duels:** Best-of-3, best-of-5 formats
- **Team battles:** Multiple models vs multiple
- **API rate limiting:** Per-user duel quotas
- **Model marketplace:** User-submitted model configurations
- **Benchmark export:** JSON/CSV export of all duel data
- **Dark mode toggle:** Light theme option
- **Mobile app:** Native iOS/Android (future)