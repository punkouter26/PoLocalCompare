# PoLocalCompare Architecture Record

## Purpose
This file is the repository-level architecture record and LLM system prompt baseline for PoLocalCompare.

## Project Identity
- Canonical solution name: `PoLocalCompare`.
- Master prefix: `PoLocalCompare` for namespaces and resources.
- UI identity:
  - Browser title must stay aligned with solution identity (`PoLocalCompare`).
  - Top-nav brand text must remain `PoLocalCompare`.
- Build identity:
  - Assembly versioning is Git tag driven via MinVer.
  - Tag prefix is `v` (example: `v1.4.0`).

## Architecture
- Server-side architecture follows Onion boundaries with physical project separation:
  - `PoLocalCompare.Domain`
  - `PoLocalCompare.Application`
  - `PoLocalCompare.Infrastructure`
  - `PoLocalCompare.Api`
- Client-side architecture uses hosted Blazor WebAssembly:
  - `Client/PoLocalCompare.Client` served by `PoLocalCompare.Api`.
- Shared DTO and contracts live in `PoLocalCompare.Shared`.

## Standards Baseline
- Global SDK pinning: .NET 10 from root `global.json`.
- Central package management: `Directory.Packages.props`.
- Global build strictness: warnings-as-errors + nullable enabled in `Directory.Build.props`.
- Blazor performance baseline:
  - `WasmEnableSIMD=true`
  - `RunAOTCompilation=false` (faster dev cycles)
- API diagnostics endpoints:
  - `/health` for automated checks
  - `/diag` for masked runtime diagnostics
  - Keep both unlinked from normal UI navigation.

## Deployment and Secret Strategy
- Primary secret source: Azure Key Vault (`kv-poshared`).
- Backup/fallback in App Service app settings for critical runtime values.
- App Service identity model: system-assigned managed identity with RBAC to storage.
- Local development storage: Azurite (`UseDevelopmentStorage=true`) with automatic dev override protection.

## Critical Settings Fallback Matrix
| Runtime key | Primary source | Backup source | Notes |
|---|---|---|---|
| `KeyVault__Uri` | App Service setting | appsettings | Bootstraps secret provider chain. |
| `ApplicationInsights__ConnectionString` / `APPLICATIONINSIGHTS_CONNECTION_STRING` | Key Vault (preferred secret shape) | App Service setting | Enables production telemetry when KV access is delayed. |
| `ConnectionStrings__AzureTableStorage` | Key Vault secret | App Service setting | Local dev uses Azurite override (`UseDevelopmentStorage=true`). |
| `ConnectionStrings__AzureBlobStorage` | Key Vault secret | App Service setting | Local dev uses Azurite override (`UseDevelopmentStorage=true`). |
| `AzureStorage__AccountName` | App Service setting | none | Used with managed identity to build service URIs without account keys. |
| `AzureAiFoundry__ApiKey` | Key Vault secret | App Service setting | Remote model duels require this key. |
| `AzureAiFoundry__Endpoint` | Key Vault secret | appsettings / App Service setting | Used by remote model availability and inference clients. |

## Automation Guardrails
- `SCRIPTS/validate-standards.ps1` validates identity/versioning invariants:
  - solution and `PoSolutionName` sync
  - root `global.json` targets .NET 10
  - `index.html` title and nav brand match solution identity
  - all `src/*/*.csproj` assembly names keep `PoLocalCompare*` prefix
  - client SIMD/AOT baseline and global strictness flags remain enforced
- This validator runs in both setup and CI to prevent standards drift.

## Authentication and Runtime Access
- Microsoft OAuth is supported in dev/prod.
- GUEST mode is dev-only and must be hidden in production.
- Guest identity persistence uses browser LocalStorage for refresh/E2E resilience.

## Observability
- Structured logging with Serilog (console + files + AI via OTel exporter path).
- OpenTelemetry traces/metrics exported to Application Insights when configured.
- Correlation fields in request logs: `UserId`, `SessionId`, `CorrelationId`.

## Tech Debt Register
- Add an explicit CI check to verify `index.html` `<title>` equals canonical solution identity.
- Add a Roslyn or build-time analyzer to enforce Po-prefix namespace/resource naming for new projects.
- Add automated policy checks for guest-auth production hardening in E2E.
- Add explicit App Service settings fallback documentation matrix for all critical keys.
- Evaluate SignalR lobby page implementation if multiplayer lobby requirements become mandatory for this app variant.

## Decision Log
- Keep Native AOT disabled for faster development loops.
- Prefer shared infra resources (`PoShared`) for App Service plan and Key Vault reuse.
- Prefer strongly typed storage DTOs over dynamic payloads for Table Storage persistence.
