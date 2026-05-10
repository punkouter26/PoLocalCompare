# Architecture: PoLocalCompare — LLM Duel Arena

## System Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Browser (WASM)                           │
│  ┌──────────┐  ┌──────────┐  ┌─────────────┐  ┌───────────┐  │
│  │ War Room │  │  Arena   │  │ Leaderboard │  │ Archive   │  │
│  └────┬─────┘  └────┬─────┘  └──────┬──────┘  └─────┬─────┘  │
│       │              │               │                │         │
│  ┌────▼──────────────▼───────────────▼────────────────▼─────┐  │
│  │           Blazor WASM (PoLocalCompare.Client)             │  │
│  │  DuelApiClient │ SignalRDuelClient │ WebLlmService        │  │
│  └──────────────────────┬────────────────────────────────────┘  │
│  Web Worker: WebLLM ────┘  (local model inference via WebGPU)   │
└──────────────────────────┬──────────────────────────────────────┘
                           │ HTTP + SignalR (WSS)
┌──────────────────────────▼──────────────────────────────────────┐
│                   PoLocalCompare.Api (ASP.NET Core)             │
│  Minimal API Endpoints + SignalR DuelHub + Scalar OpenAPI       │
├──────────────────────────────────────────────────────────────────┤
│              PoLocalCompare.Application                          │
│  CommenceDuel │ RecordVerdict │ GetLeaderboard │ ExportReport   │
├──────────────────────────────────────────────────────────────────┤
│              PoLocalCompare.Infrastructure                       │
│  TableStorage │ FoundryProxy │ GreenStats │ RazorRenderer       │
├──────────────────────────────────────────────────────────────────┤
│              PoLocalCompare.Domain                               │
│  Entities │ Value Objects │ EloCalculator │ Enums               │
└──────────────────────────────────────────────────────────────────┘
           │                          │
┌──────────▼──────────┐   ┌──────────▼─────────────┐
│  Azure Table Storage│   │  Azure AI Foundry       │
│  Models | Duels     │   │  (remote model proxy)   │
│  DuelResults | Elo  │   └────────────────────────┘
│  + Blob Storage     │
└─────────────────────┘
```

## Data Flow: Duel Lifecycle

1. **War Room** → User selects models + enters prompt → `POST /api/duels`
2. **Server** → Creates `Duel` entity, returns `duelId` (202 Accepted)
3. **Client** → Subscribes to SignalR hub `/hubs/duel` group `duel-{id}`
4. **Client** → Starts WebLLM worker (local model) AND server-side Foundry call (remote model) concurrently
5. **SignalR** → Server pushes `ModelStatusUpdate` events every 500ms (token counts, elapsed time)
6. **Client** → Forwards local model result to `POST /api/duels/{id}/left-result`
7. **Server** → Stores both results, updates `DuelStatus = Done`
8. **Arena** → Loads `GET /api/duels/{id}` → shows dual HTML viewports
9. **User** → Picks winner → `POST /api/duels/{id}/verdict`
10. **Server** → Runs `EloCalculator`, appends `EloRecord`, broadcasts `EloUpdated`

## Storage Schema

| Table | PK | RK | Purpose |
|-------|----|----|---------|
| Models | `"model"` | `{modelId}` (ULID) | Model registry |
| Duels | `YYYYMM` | `{duelId}` (ULID) | Duel sessions |
| DuelResults | `{duelId}` | `{modelId}` | Per-model telemetry |
| EloHistory | `{modelId}` | inverted-tick | ELO snapshots (sparklines) |

## Observability

- **Serilog**: File + Console + App Insights sinks; enriched with UserId, SessionId, CorrelationId
- **OpenTelemetry**: Traces + Metrics exported to App Insights (`PoShared` resource)
- **`/health`**: Pings Table Storage + Foundry + Key Vault
- **`/diag`**: Blazor page showing all connection statuses + config (masked)
