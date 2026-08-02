# PRD_Master — PoLocalCompare (Project Source of Truth)

> Supersedes the former `docs/PRD.md` and `docs/adr/` set (2026-07-06). ADR history is preserved in §9.

---

## 1. Product Definition

**PoLocalCompare** is a real-time LLM benchmarking arena. Two models — **Local** (WebLLM/WebGPU in the browser), **Remote** (Azure AI Foundry), or **LocalService** (Ollama, dev-only) — race to generate HTML from the same prompt. Output streams live over SignalR; a human picks the winner, or an AI judge decides if nobody picks within `AiJudge:DelaySeconds` (§9 item 9); Elo ratings (K=32, start 1200) update in Azure Table Storage, tagged with the verdict's source.

- **Live:** https://polocalcompare.azurewebsites.net (Azure App Service F1, Windows, single origin)
- **Who:** a single hobbyist operator plus invited guests; mobile-portrait-first UX.
- **Why:** answer "is a free local browser model actually competitive with a paid cloud model?" with measured evidence — speed, quality, energy (Green Score = tokens/Wh) and API cost.

## 2. Vertical Slice Boundaries

All server code lives in `PoLocalCompare.Api` (VSA). Each slice is flat: endpoints + handlers + entities + repository in one folder. Cross-slice code is quarantined in `Common/`.

| Slice | Folder | Owns |
|---|---|---|
| **Duels** | `Features/Duels/` | `Duel`, `DuelResult`, commence/get/list/verdict handlers, `DuelExecutionService`, `DuelHub`, repositories |
| **Leaderboard** | `Features/Leaderboard/` | `EloRecord`, `EloCalculator` (K=32), kill-list + leaderboard handlers, `EloHistoryRepository`, HybridCache tag `leaderboard` |
| **Models** | `Features/Models/` | `Model` entity, register/delete, `GetModelAvailabilityHandler` probes, `DownloadModelHandler`, `ModelSeeder` |
| **Archive** | `Features/Archive/` | Lab-report export (`ArchiveEndpoints` + `ExportLabReportHandler` + static `HtmlLabReportRenderer`) |
| **Ollama** | `Features/Ollama/` | GPU status, available-models and benchmark handlers (dev-only value) |
| **Diagnostics** | `Features/Diagnostics/` | `/health`, `/api/diag/*` |
| *(cross-slice)* | `Common/` | Domain calculators, inference proxies, background queue, Key Vault, Azurite bootstrap, `RateLimitedSampler` |
| *(cross-slice)* | `Auth/` | BFF cookie session, Microsoft OIDC, `FakeAuthHandler` (non-prod) |

`PoLocalCompare.Shared` is **restricted to DTOs and enums** consumed by both WASM and API — no logic, no entities. `PoLocalCompare.Client` (Blazor WASM) is hosted by the API (single-origin, no CORS).

## 3. API Endpoint Map

All groups `RequireAuthorization()` (deny-by-default fallback policy); anonymous routes are explicit.

| Method + Route | Slice | Notes |
|---|---|---|
| `POST /api/duels` | Duels | 202 + Location; enqueues background execution. Optional `autoJudgeDelaySeconds` overrides the grace window for that duel only (clamped 0–3600) |
| `GET /api/duels` | Duels | Archive listing, `limit` clamped 1–100, `before` paging |
| `GET /api/duels/demo-plan?rounds=&seed=` | Duels | Read-only; resolves the pairings + prompts for a demo run. Remote models only, `rounds` clamped 1–25 |
| `GET /api/duels/{duelId}` | Duels | Full telemetry DTO |
| `POST /api/duels/{duelId}/local-result` | Duels | WebLLM browser result ingest + Domain enrichment |
| `POST /api/duels/{duelId}/verdict` | Duels | Elo update; ETag 412 retry-once; invalidates leaderboard cache |
| `GET /api/duels/{duelId}/report` | Archive | Self-contained HTML download |
| `GET /api/leaderboard?sortBy=` | Leaderboard | HybridCache, tag-invalidated |
| `GET /api/leaderboard/{modelId}/killlist` | Leaderboard | Head-to-head aggregates |
| `GET /api/leaderboard/h2h/{modelIdA}/{modelIdB}` | Leaderboard | Full record between one pair; 404 on unknown or identical ids. Record is exact, telemetry averages are sampled from the last 10 meetings |
| `GET /api/models` | Models | LocalService hidden outside Development |
| `GET /api/models/availability` | Models | Probes Ollama tags + Foundry deployments |
| `POST /api/models` · `PATCH/DELETE /api/models/{id}` | Models | Registry CRUD |
| `GET /api/models/download-status/{webLlmModelId}` | Models | Path-traversal-guarded asset check |
| `POST /api/models/{webLlmModelId}/download` | Models | 202; detached python HuggingFace download |
| `GET /api/ollama/gpu-status` · `/available-models` · `POST /benchmark` | Ollama | Local-only value; failures return empty/failure DTOs |
| `GET /auth/me` · `/auth/login/microsoft` · `/auth/login/fake`¹ · `POST /auth/logout` | Auth | Anonymous; ¹non-Production only |
| `GET /health` · `/api/diag/smoke` · `/api/diag/warnings` · `/diag` (Razor) | Diagnostics | Anonymous; no UI links |
| `/hubs/duel` | SignalR | `RequireAuthorization()`. Client-invokable: `JoinDuel(duelId)`, `JoinLobby()` — both subscribe only. Server→client: `ModelStatusUpdate`, `DuelComplete`, `StartLocalInference`, `VerdictRecorded`, `LobbyEvent` |
| `POST /api/dev/reset` · `/scalar` · `/openapi` | Dev-only | Development host only |

## 4. Data (Azure Table Storage — see DatabaseSchema.mmd)

| Table | PartitionKey | RowKey | Purpose |
|---|---|---|---|
| `Duels` | `YYYYMM` (from ULID timestamp) | `DuelId` (ULID, lexicographically time-ordered) | Aggregate root + verdict |
| `DuelResults` | `DuelId` | `ModelId` | Per-model telemetry (2 rows/duel) |
| `Models` | `"model"` (single partition) | `ModelId` | Registry + denormalized Elo/W-L stats |
| `EloHistory` | `ModelId` | `{invertedTicks:D19}_{DuelId}` (descending time) | Immutable rating ledger, sparklines |

Write discipline (standards §5.5): creates swallow 409; updates are `If-Match` conditional via carried `ETag`; duel writers re-read + reapply on 412.

`Duels` also carries `VerdictSource` (`Human`/`Ai`), `JudgeRationale` and `JudgeModel` — see §9 item 9. Rows written before those columns existed read back as `Human`, which is what they were.

## 5. Trimmer-Compatible Model Criteria

- `PoLocalCompare.Shared` and `PoLocalCompare.Client` set `<IsTrimmable>true</IsTrimmable>` + `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>`.
- The app **publishes untrimmed** (`<PublishTrimmed>false</PublishTrimmed>`): Router `NotFoundPage` instantiates components reflectively, which the IL trimmer strips (`CtorNotLocated`). Radzen and MSAL used to be named here too; neither is referenced any more, so `NotFoundPage` is the only remaining blocker. Trim diagnostics surface as warnings (IL2xxx/IL3xxx scoped out of `TreatWarningsAsErrors`).
- Criteria for DTOs/entities staying trim-safe: parameterless ctor + settable properties for Table Storage rehydration; no reflection-only members; `JsonStringEnumConverter` registered centrally; source-generated serialization not yet required.

## 6. Zero-Allocation Logging Standard

Hot paths use source-generated `[LoggerMessage]` partials (e.g. `DuelExecutionLog` in `Features/Duels/DuelExecutionService.cs`) — no boxing, no format parsing at runtime. Serilog remains the pipeline: Console always; rolling files Development-only; Error+ events ship to App Insights unsampled. Traces: `AlwaysOnSampler` outside Production, `RateLimitedSampler` (`Telemetry:MaxTracesPerSecond`, default 10/s) in Production. Client stamps `X-Session-ID`/`X-Correlation-ID` via `CorrelationHandler`.

## 7. Authentication Contract (BFF)

Server owns the OIDC code flow (PKCE, authority `login.microsoftonline.com/common/v2.0`, callback `/signin-oidc`). Session = encrypted `PoLocalCompare.Session` cookie (HttpOnly, `SameSite=Strict`, Secure, 8 h sliding). WASM never sees tokens; it reads `GET /auth/me` (never 401s). Unauthenticated API calls get **401 JSON, never 302**. Issuer validation: explicit `AzureAd:AllowedTenants` list, else shape-based Entra issuer regex (accepts personal MSA tenant). Non-prod adds guest login (`/auth/login/fake`) and header-driven `FakeAuthHandler` (`X-Fake-User`/`X-Fake-Roles`; throws if constructed in Production).

## 8. Non-Functional Budget

- **Zero-waste hosting:** F1 App Service, single origin, no CORS, tag-invalidated HybridCache, telemetry rate cap.
- **Fail-fast startup:** non-dev host verifies Table Storage reachability within 15 s or exits.
- **Duel watchdog:** 900 s inference cap (WebGPU shader JIT headroom). Every duel stays Pending until a human picks a winner — a failed model is never auto-awarded against.
- **Errors:** RFC 7807 `problem+json` globally; correlation id echoed.
- **Mock visibility:** `USING MOCK DATA` banner when `Features:UseRealAi` is off.

## 9. Decision Log (ADR summary)

1. **0001 (superseded):** launched on Clean/Onion (Domain/Application/Infrastructure projects) for testability.
2. **0002 (2026-07-06, active):** collapsed to Vertical Slice Architecture per the global Po* mandate — Domain/Application/Infrastructure merged into `Api/Features/*` + `Api/Common/*`; only `.API`/`.Client`/`.Shared` src projects remain.
3. **Managed identity (2026-07-06):** system-assigned MI; Bicep grants Storage Table/Blob RBAC + `kv-poshared` access policy; secrets use the `PoLocalCompare--` Key Vault prefix.
4. **CI policy (2026-07-06, superseded by item 12):** pipeline builds/publishes/deploys only — tests are never run in CI; infra Bicep runs only when the App Service is missing.
5. **Test consolidation (2026-07-26, superseded by item 11):** the four test assemblies (UnitTests / IntegrationTests / E2EAPI / E2EUI) collapsed into two — `PoLocalCompare.Tests` (`Unit/`, `Integration/`) and `PoLocalCompare.Tests.E2E` (`Api/`, `Ui/`). UI tests carry `[Trait("Category","UI")]` so the API journeys can still run alone. The 100/50/25/25 ratio is tracked in AGENT.MD §8.
6. **Language version (2026-07-26):** `LangVersion=latest` rather than a pinned `15` — the standards mandate C# 15, but SDK 10.0.301 rejects it (CS1617); `latest` picks it up as soon as the toolchain ships it.
7. **Human-only verdicts (2026-07-26):** the forfeit auto-award was removed from `DuelExecutionService`. No code path may record a verdict or move ELO without a human decision in the Arena; a failed model is judged, not auto-resolved. The Arena gained a **Retry duel** action that re-runs the same pairing and prompt as a new duel, since most failures are transient.
8. **Offline browser-model artifacts (2026-07-26):** WebLLM weights are vendored into `wwwroot/models/` rather than streamed from the CDN at run time. The dev network is not blocked but is *unreliable* to huggingface.co: Windows schannel cannot reach the certificate revocation responder (`CRYPT_E_REVOCATION_OFFLINE`) and roughly half of new TLS connections are reset upstream, which `huggingface_hub` and Python's TLS stack both fail on immediately. `SCRIPTS/download-models.py` therefore shells out to `curl` with `--retry`/`--ssl-no-revoke` and byte-range resume, verifying each file against the sha256 the Hub advertises so the relaxed revocation check costs no integrity — established connections run at full speed, so this is the primary path. `.github/workflows/fetch-webllm-artifacts.yml` remains the fallback for a genuinely blocked network: it fetches on a GitHub runner and publishes split `tar` parts to a `webllm-artifacts` prerelease for `SCRIPTS/receive-artifacts.ps1` to install. Release assets rather than Actions artifacts — artifacts count against repo storage and download as one unresumable zip. Either path also vendors the `.wasm` model libraries into `wwwroot/models/_libs/`, because `prebuiltAppConfig.model_lib` points at raw.githubusercontent.com — a separate host, so serving weights locally is not on its own sufficient. `webllm-worker.js` prefers `_libs/` and falls back to the CDN. All three scripts derive the model list from `ModelSeeder.cs` + `web-llm.js` via `SCRIPTS/plan-webllm-artifacts.py`, so they cannot drift.
9. **Auto-judge reinstated, with attribution (2026-07-26):** item 7 above is **reversed**. When no human picks a winner within `AiJudge:DelaySeconds` (default 5) of a duel finishing, `AutoJudge` asks a Foundry model which output implements the prompt more accurately and records that verdict, moving ELO. What makes this safe to reverse is that verdicts are no longer anonymous: `Duel.VerdictSource` distinguishes `Human` from `Ai`, with `JudgeRationale` and `JudgeModel` alongside, so an LLM-ranked leaderboard can be separated from a human-ranked one after the fact — blending them irreversibly was the real risk, not automation itself. Three invariants: a human decision always wins the race (the judge re-reads and stands down on anything but `Pending`; `RecordVerdictHandler` throws on a second verdict); a judge that cannot decide leaves the duel `Pending` rather than guessing, so ELO never moves on no evidence; and `AiJudge:Enabled=false` restores item 7's behaviour exactly. A duel where only one model produced output is awarded to the survivor without spending a judge call, with the rationale recording that it was a walkover. LLM judges show a documented position bias, so the two outputs are assigned to slots A/B by a coin flip per duel and mapped back — the bias then lands on Left and Right equally instead of systematically favouring Left; length and self-preference bias are not corrected for. The judge runs inline at the end of the duel's own queued work item because `BackgroundTaskService` awaits each item before dequeuing the next, so a queued delay would stall the following duel. Note that at 5 seconds a person cannot realistically read both outputs, so in practice nearly every duel is LLM-judged; `DelaySeconds` is the dial for that.
10. **Accessibility (2026-07-26):** WCAG 2.2 Level AA adopted as the UI contract — global focus-visible ring, 24×24 minimum target size (SC 2.5.8), skip link (SC 2.4.1), sticky-nav scroll clearance (SC 2.4.11), keyboard-operable model cards and wizard steps (SC 2.1.1), and `role="status"` live regions (SC 4.1.3). Contrast ratios remain a manual check.
11. **Test split restored (2026-08-01):** item 5 is **reversed**. NET_RULES §2 mandates four test projects — `PoLocalCompare.Unit`, `.Integration`, `.E2EAPI`, `.E2EUI` — so the folder-slices are assemblies again. The consolidation in item 5 read the standard as mandating two; it does not. The split buys something concrete beyond conformance: `PoLocalCompare.Unit` no longer references `Testcontainers` or `Microsoft.AspNetCore.Mvc.Testing`, so "unit tests need no Docker" is enforced by the project graph instead of by a `--filter FullyQualifiedName~Unit` convention that nothing checked. `PoLocalCompare.E2EUI` drops its `ProjectReference` entirely and drives the app over HTTP, which stops UI tests from silently coupling to server internals. `[Trait("Category","UI")]` is retained but is no longer load-bearing for suite selection.

12. **Tests gate deploy (2026-08-01):** item 4 is **reversed** for the three server-side tiers. A `test` job now runs `PoLocalCompare.Unit`, `.Integration` and `.E2EAPI` and `build` depends on it, so a red suite blocks the deploy instead of shipping. Testcontainers works unmodified on the ubuntu runner. `PoLocalCompare.E2EUI` stays out: it drives a real headed Chrome and exercises WebGPU paths a runner has no GPU for, so it would be flaky rather than informative — it remains a local gate after UI changes. The infra-Bicep half of item 4 is unchanged.

13. **Client-side comparison tooling (2026-08-02):** the Arena gained three things that are computed in the browser and never reach storage — a Rendered/Code/Diff switch (`SourceCompare`, LCS line diff with identical runs folded), a structural scorecard (`OutputAnalysis`), and an optimistic verdict that paints the winner on the frame of the click. Two constraints shaped this. First, the analysis is deliberately *separate* from the persisted `OutputQualityScore`: tightening a heuristic here must never retroactively change a stored duel, so `CompletenessScore` is presentational only and nothing on the scorecard touches ELO. Second, the runtime half — thrown errors, failed CDN assets, load time — needs the sandboxed frame to report back, which means prepending a probe `<script>` into the iframe's `srcdoc`. That injection is confined to the *preview*: the raw output is what gets persisted, analysed, diffed and shown by "View Source", so nothing a person judges or exports contains it. The frame has an opaque origin (`sandbox="allow-scripts"` with no `allow-same-origin`), so `postMessage` is the only channel out and `ev.origin` is always `"null"` and cannot authenticate — each frame is instead handed a random id and only registered ids are dispatched. The optimistic verdict paints winner/loser immediately but deliberately leaves ELO blank until the server responds, because inventing a rating change would be a guess presented as a result; if the auto-judge wins the race, the Arena states plainly that the pick was not counted rather than swapping the winner silently.

14. **Demo mode and the auto-judge override (2026-08-02):** `/demo` runs ten duels back to back, unattended. They are **ordinary duels** — persisted, archived, auto-judged, and they move ELO — which is stated on the page before the start button rather than discovered afterwards on the leaderboard; the alternative (an `IsDemo` flag excluded from ELO) was rejected as it would put a second write path around the one choke point CLAUDE.md guards. The pool is **remote models only**: browser models run inference in the client's tab and stall without a GPU or with the tab backgrounded, and Ollama models seed in Development only, so neither is safe for "press play and walk away". `DemoPlanner` is pure and seeded, so the whole schedule — pairings and prompts — is resolved and displayed before anything is written; it flips left/right per round so that any position bias in the judge lands on both models equally rather than reading as a fact about one. Rounds run sequentially because `BackgroundTaskService` awaits each queued item anyway, so firing ten at once would queue behind each other with nothing to watch but the first. `POST /api/duels` gained an optional `autoJudgeDelaySeconds`, which the demo sets to 0; it is clamped 0–3600 and cannot enable the judge — `AiJudge:Enabled=false` still restores human-only verdicts (decision 9 stands).

15. **Global activity ticker (2026-08-02):** `DuelHub` gained a second group, `lobby`, and `LobbyNotifier` publishes duel-started / duel-completed / verdict-recorded to it. The verdict announcement is emitted from inside `RecordVerdictHandler` rather than from its two callers, because that handler is the only path by which ELO moves — announcing there is what makes the feed complete by construction instead of by remembering to add a call at each site. Every notifier method swallows its own failures: the ticker is ambient decoration on the nav bar and a SignalR hiccup must never fail a duel or a verdict. `JoinLobby()` subscribes only and grants no ability to push, matching `JoinDuel`. The client's "awaiting judgment" count is seeded once from `/api/duels` on connect, because a live-events-only counter would read zero on every fresh tab regardless of the real backlog.

16. **Dead-code and dependency prune (2026-08-02):** a single sweep across the solution, worth recording because three of its removals change contracts. **Radzen.Blazor is gone** — it was carried for `RadzenDataGrid` (3 uses) and `RadzenButton` (14), and is replaced by `.po-btn` / `.po-table` in `app.css`, built from the existing design tokens. That also removed the per-theme Radzen stylesheet and the `theme.js` branch that swapped its `<link>` on every toggle, so theming is now entirely custom properties; the `System.Linq.Dynamic.Core` CVE pin went with it, being a Radzen transitive. Converting the grids to semantic tables was a net accessibility gain (tabbable rows, `Enter`/`Space` activation, captions, `aria-pressed` on sort buttons) because `RadzenDataGrid` had been providing none of it. **`/processing` is merged into `/arena`.** A duel streamed on one route and was judged on another, which meant a full remount of both preview iframes at the exact moment the outputs became worth looking at; Arena now holds a running state and the browser-model inference driver that used to live on the Processing page. `Pages/Processing.razor` and `Components/ProcessingPanel.razor` are deleted — `TokenRace` plus a single stall hint carry the live read-out, and `TelemetryHud` already covered the post-run numbers. **Unused API surface removed:** `PATCH /api/models/{id}` (no UI; the catalog is seeded), `GET /api/models/download-status/{id}` (answered client-side by the `checkModelFile` probe all along), and `/e2e/seed-auth` (a redirect to `/auth/login/fake`, which the E2E tests now call directly). The Lab Report went the other way: it was a finished slice with no button, so Arena and Archive now link it. Also dropped: `Azure.AI.OpenAI` and `Azure.AI.Inference` package references with no code behind them (all inference is raw `HttpClient` + SSE), the `GreenScore` value object that was never instantiated, and 106 of 147 selectors in `ModelHealthPanel.razor.css` — 63 belonging to the benchmark UI that was removed when the Model Lab page was folded into the panel, and 43 that had been silently inert because they styled `LabModelCard`'s markup from a *different* component's scoped stylesheet.

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
