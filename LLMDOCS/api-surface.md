# API Surface: PoLocalCompare — LLM Duel Arena

Base URL (local): `https://localhost:5001`
OpenAPI: `https://localhost:5001/scalar`
Health: `https://localhost:5001/health`
Diagnostics: `https://localhost:5001/diag`

## REST Endpoints

### Models

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/models` | List all models with ELO + metadata |
| POST | `/api/models` | Register a new model |

### Duels

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/duels` | Commence a new duel (returns 202 + duelId) |
| GET | `/api/duels/{id}` | Get duel details + results |
| GET | `/api/duels` | List duels (paginated, Archive) |
| POST | `/api/duels/{id}/verdict` | Record user verdict + trigger ELO update |
| GET | `/api/duels/{id}/report` | Download Lab Report HTML |

### Leaderboard

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/leaderboard` | ELO-ranked list with sparklines + Green Score |
| GET | `/api/leaderboard/{modelId}/kill-list` | Models this model has beaten |

### Infrastructure

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health` | Health check (Table Storage + Foundry + Key Vault) |
| GET | `/diag` | Diagnostics Blazor page |
| GET | `/scalar` | OpenAPI / Scalar UI |

## SignalR Hub

**Endpoint**: `/hubs/duel`  
**Group**: `duel-{duelId}` (join via `JoinDuel(duelId)`)

| Message | Direction | Payload |
|---------|-----------|---------|
| `ModelStatusUpdate` | Server → Client | `{ duelId, modelId, side, status, tokenCount, elapsedMs }` |
| `DuelCompleted` | Server → Client | `{ duelId }` |
| `EloUpdated` | Server → Client | `{ leftModelId, rightModelId, leftNewElo, rightNewElo }` |

## Key DTOs (PoLocalCompare.Shared)

- `ModelDto` — model registry entry
- `DuelDto` — full duel with both models + results
- `DuelSummaryDto` — lightweight duel for Archive list
- `DuelResultDto` — per-model telemetry
- `LeaderboardEntryDto` — ELO row with sparkline data
- `VerdictRequestDto` / `VerdictResponseDto` — verdict submission
- `ModelStatusUpdateDto` — SignalR message shape
