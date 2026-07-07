# PRD_Master — PoLocalCompare (Project Source of Truth)

> Supersedes the former `docs/PRD.md` and `docs/adr/` set (2026-07-06). ADR history is preserved in §9.

---

## 1. Product Definition

**PoLocalCompare** is a real-time LLM benchmarking arena. Two models — **Local** (WebLLM/WebGPU in the browser), **Remote** (Azure AI Foundry), or **LocalService** (Ollama, dev-only) — race to generate HTML from the same prompt. Output streams live over SignalR; a human judge picks the winner (or the surviving model wins by forfeit when one fails); Elo ratings (K=32, start 1200) update in Azure Table Storage.

- **Live:** https://polocalcompare.azurewebsites.net (Azure App Service F1, Windows, single origin)
- **Who:** a single hobbyist operator plus invited guests; mobile-portrait-first UX.
- **Why:** answer "is a free local browser model actually competitive with a paid cloud model?" with measured evidence — speed, quality, energy (Green Score = tokens/Wh) and API cost.

## 2. Vertical Slice Boundaries

All server code lives in `PoLocalCompare.Api` (VSA). Each slice is flat: endpoints + handlers + entities + repository in one folder. Cross-slice code is quarantined in `Common/`.

| Slice | Folder | Owns |
|---|---|---|
| **Duels** | `Features/Duels/` | `Duel`, `DuelResult`, commence/get/list/verdict handlers, `DuelExecutionService` (forfeit auto-award), `DuelHub`, repositories |
| **Leaderboard** | `Features/Leaderboard/` | `EloRecord`, `EloCalculator` (K=32), kill-list + leaderboard handlers, `EloHistoryRepository`, HybridCache tag `leaderboard` |
| **Models** | `Features/Models/` | `Model` entity, registry CRUD, availability probes, WebLLM download status/trigger, `ModelSeeder` |
| **Archive** | `Features/Archive/` | Lab-report export (`ExportLabReportHandler` + `HtmlLabReportRenderer`) |
| **Ollama** | `Features/Ollama/` | GPU status, available-models, benchmark endpoints (dev-only value) |
| **Diagnostics** | `Features/Diagnostics/` | `/health`, `/api/diag/*`, E2E helpers (non-prod) |
| *(cross-slice)* | `Common/` | Domain calculators, inference proxies, background queue, Key Vault, Azurite bootstrap, `RateLimitedSampler` |
| *(cross-slice)* | `Auth/` | BFF cookie session, Microsoft OIDC, `FakeAuthHandler` (non-prod) |

`PoLocalCompare.Shared` is **restricted to DTOs and enums** consumed by both WASM and API — no logic, no entities. `PoLocalCompare.Client` (Blazor WASM) is hosted by the API (single-origin, no CORS).

## 3. API Endpoint Map

All groups `RequireAuthorization()` (deny-by-default fallback policy); anonymous routes are explicit.

| Method + Route | Slice | Notes |
|---|---|---|
| `POST /api/duels` | Duels | 202 + Location; enqueues background execution |
| `GET /api/duels` | Duels | Archive listing, `limit` clamped 1–100, `before` paging |
| `GET /api/duels/{duelId}` | Duels | Full telemetry DTO |
| `POST /api/duels/{duelId}/local-result` | Duels | WebLLM browser result ingest + Domain enrichment |
| `POST /api/duels/{duelId}/verdict` | Duels | Elo update; ETag 412 retry-once; invalidates leaderboard cache |
| `GET /api/duels/{duelId}/report` | Archive | Self-contained HTML download |
| `GET /api/leaderboard?sortBy=` | Leaderboard | HybridCache, tag-invalidated |
| `GET /api/leaderboard/{modelId}/killlist` | Leaderboard | Head-to-head aggregates |
| `GET /api/models` | Models | LocalService hidden outside Development |
| `GET /api/models/availability` | Models | Probes Ollama tags + Foundry deployments |
| `POST /api/models` · `PATCH/DELETE /api/models/{id}` | Models | Registry CRUD |
| `GET /api/models/download-status/{webLlmModelId}` | Models | Path-traversal-guarded asset check |
| `POST /api/models/{webLlmModelId}/download` | Models | 202; detached python HuggingFace download |
| `GET /api/ollama/gpu-status` · `/available-models` · `POST /benchmark` | Ollama | Local-only value; failures return empty/failure DTOs |
| `GET /auth/me` · `/auth/login/microsoft` · `/auth/login/fake`¹ · `POST /auth/logout` | Auth | Anonymous; ¹non-Production only |
| `GET /health` · `/api/diag/smoke` · `/api/diag/warnings` · `/diag` (Razor) | Diagnostics | Anonymous; no UI links |
| `/hubs/duel` | SignalR | `RequireAuthorization()` |
| `POST /api/dev/reset` · `/scalar` · `/openapi` | Dev-only | Development host only |

## 4. Data (Azure Table Storage — see DatabaseSchema.mmd)

| Table | PartitionKey | RowKey | Purpose |
|---|---|---|---|
| `Duels` | `YYYYMM` (from ULID timestamp) | `DuelId` (ULID, lexicographically time-ordered) | Aggregate root + verdict |
| `DuelResults` | `DuelId` | `ModelId` | Per-model telemetry (2 rows/duel) |
| `Models` | `"model"` (single partition) | `ModelId` | Registry + denormalized Elo/W-L stats |
| `EloHistory` | `ModelId` | `{invertedTicks:D19}_{DuelId}` (descending time) | Immutable rating ledger, sparklines |

Write discipline (standards §5.5): creates swallow 409; updates are `If-Match` conditional via carried `ETag`; duel writers re-read + reapply on 412.

## 5. Trimmer-Compatible Model Criteria

- `PoLocalCompare.Shared` and `PoLocalCompare.Client` set `<IsTrimmable>true</IsTrimmable>` + `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>`.
- The app **publishes untrimmed** (`<PublishTrimmed>false</PublishTrimmed>`): Router `NotFoundPage`, Radzen, and MSAL instantiate components reflectively, which the IL trimmer strips (`CtorNotLocated`). Trim diagnostics surface as warnings (IL2xxx/IL3xxx scoped out of `TreatWarningsAsErrors`).
- Criteria for DTOs/entities staying trim-safe: parameterless ctor + settable properties for Table Storage rehydration; no reflection-only members; `JsonStringEnumConverter` registered centrally; source-generated serialization not yet required.

## 6. Zero-Allocation Logging Standard

Hot paths use source-generated `[LoggerMessage]` partials (e.g. `DuelExecutionLog` in `Features/Duels/DuelExecutionService.cs`) — no boxing, no format parsing at runtime. Serilog remains the pipeline: Console always; rolling files Development-only; Error+ events ship to App Insights unsampled. Traces: `AlwaysOnSampler` outside Production, `RateLimitedSampler` (`Telemetry:MaxTracesPerSecond`, default 10/s) in Production. Client stamps `X-Session-ID`/`X-Correlation-ID` via `CorrelationHandler`.

## 7. Authentication Contract (BFF)

Server owns the OIDC code flow (PKCE, authority `login.microsoftonline.com/common/v2.0`, callback `/signin-oidc`). Session = encrypted `PoLocalCompare.Session` cookie (HttpOnly, `SameSite=Strict`, Secure, 8 h sliding). WASM never sees tokens; it reads `GET /auth/me` (never 401s). Unauthenticated API calls get **401 JSON, never 302**. Issuer validation: explicit `AzureAd:AllowedTenants` list, else shape-based Entra issuer regex (accepts personal MSA tenant). Non-prod adds guest login (`/auth/login/fake`) and header-driven `FakeAuthHandler` (`X-Fake-User`/`X-Fake-Roles`; throws if constructed in Production).

## 8. Non-Functional Budget

- **Zero-waste hosting:** F1 App Service, single origin, no CORS, tag-invalidated HybridCache, telemetry rate cap.
- **Fail-fast startup:** non-dev host verifies Table Storage reachability within 15 s or exits.
- **Duel watchdog:** 900 s inference cap (WebGPU shader JIT headroom). A duel where one model fails is auto-awarded to the survivor; otherwise it stays Pending until a human picks a winner.
- **Errors:** RFC 7807 `problem+json` globally; correlation id echoed.
- **Mock visibility:** `USING MOCK DATA` banner when `Features:UseRealAi` is off.

## 9. Decision Log (ADR summary)

1. **0001 (superseded):** launched on Clean/Onion (Domain/Application/Infrastructure projects) for testability.
2. **0002 (2026-07-06, active):** collapsed to Vertical Slice Architecture per the global Po* mandate — Domain/Application/Infrastructure merged into `Api/Features/*` + `Api/Common/*`; only `.API`/`.Client`/`.Shared` src projects remain.
3. **Managed identity (2026-07-06):** system-assigned MI; Bicep grants Storage Table/Blob RBAC + `kv-poshared` access policy; secrets use the `PoLocalCompare--` Key Vault prefix.
4. **CI policy:** pipeline builds/publishes/deploys only — tests are never run in CI; infra Bicep runs only when the App Service is missing.

## 10. Diagram Index (this folder)

Each `.mmd` has a `*_simplified.mmd` twin and a rendered `.svg`.

| Diagram | Type | Shows |
|---|---|---|
| `User_Journey` | journey | Mobile-portrait cognitive tasks vs perceived performance |
| `UI_Screen_Matrix` | stateDiagram-v2 | Routes, `AuthorizeRouteView` gating, layout-flash mitigations |
| `Flow_Identity_BFF` | graph TD | OIDC challenge, cookie loop, `/auth/me` gates |
| `Flow_Validation_Failures` | graph TD | Duels-slice validation pipeline UI→domain→storage |
| `Architecture_VSA_Blueprint` | graph TD | Slice isolation, Client bounds, restricted Shared |
| `Interaction_Trace` | sequenceDiagram | Razor → typed HttpClient → BFF cookie → handler → Table Storage |
| `DatabaseSchema` | erDiagram | Tables, partition/row keys, relations |
