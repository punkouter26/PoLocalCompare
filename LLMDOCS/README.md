# PoLocalCompare — LLM Duel Arena

## Quick Orientation

PoLocalCompare is a benchmarking platform that pits **local browser-based LLMs** (via WebLLM/WebGPU) against **remote cloud models** (via Azure AI Foundry) in timed HTML-generation duels. An Elo rating system (K=32) tracks performance over time.

## Architecture Overview

```
Onion Architecture (server):
  Domain → Application → Infrastructure → Api

Client:
  Blazor WASM (hosted in Api) + Radzen UI
```

## Project Layout

| Project | Purpose |
|---------|---------|
| `PoLocalCompare.Domain` | Entities, Value Objects, Domain Services, Enums — no external deps |
| `PoLocalCompare.Application` | Use cases (CQRS-style), Application Interfaces |
| `PoLocalCompare.Infrastructure` | Repository implementations, Azure Table Storage, AI Foundry proxy, Key Vault |
| `PoLocalCompare.Api` | ASP.NET Core minimal API host, SignalR hub, Blazor host |
| `PoLocalCompare.Shared` | DTOs and enums safe for both server and WASM client |
| `PoLocalCompare.Client` | Blazor WASM client — 4 pages + Web Worker for local LLM inference |

## Key Files

- `src/PoLocalCompare.Api/Program.cs` — DI wiring, middleware, Serilog, OpenTelemetry
- `src/PoLocalCompare.Api/appsettings.json` — non-secret configuration
- `specs/001-llm-duel-arena/plan.md` — full technical plan
- `specs/001-llm-duel-arena/contracts/api.md` — REST + SignalR contracts
- `specs/001-llm-duel-arena/data-model.md` — entity definitions

## Development Setup

1. Install .NET 10 SDK
2. Start Azurite: `docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite`
3. Run: `dotnet run --project src/PoLocalCompare.Api --launch-profile https`
4. Open: `https://localhost:5001`
5. Scalar UI: `https://localhost:5001/scalar`
6. Health: `https://localhost:5001/health`

## Design Patterns in Use

- **GoF: Repository** — `Infrastructure/Persistence/TableStorage/`
- **GoF: Proxy** — `FoundryInferenceProxy.cs`
- **SOLID: Dependency Inversion** — All application interfaces in `Application/Interfaces/`
- **SOLID: Single Responsibility** — `EloCalculator.cs` pure formula only
- **GoF: Observer** — Real-time updates via SignalR `DuelHub`
