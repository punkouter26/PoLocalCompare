# Research: PoLocalCompare — LLM Duel Arena

**Phase**: 0 — Unknowns Resolution
**Date**: 2026-05-09
**Input**: spec.md clarifications + technical stack provided by user

---

## 1. Local Inference Runtime

**Decision**: WebLLM (MLC-AI) via WebGPU API, run inside a dedicated Web Worker.

**Rationale**: The user explicitly specified WebLLM with the MLC-AI ecosystem. Running inference in a Web Worker prevents UI blocking, keeps the 5-minute watchdog timer independent of the rendering thread, and allows the processing-phase HUD (token count, elapsed time) to update via `postMessage` callbacks without thread contention.

**Model weights management**: Browser Cache API — weights (e.g., Gemma 4, Phi-4) are fetched once and cached; subsequent runs skip the download, reducing warm-up time to GPU-load-only.

**WebGPU availability**: The Blazor WASM client must detect WebGPU support at startup and surface the result on the `/diag` page. If WebGPU is unavailable (unsupported browser), local model selection must be disabled with a clear explanatory message.

**Alternatives considered**: Ollama (local server) — rejected because it requires a separate user-managed process and contradicts the "browser environment" spec language. ONNX Runtime Web — rejected in favour of WebLLM as the user explicitly named it.

---

## 2. Remote Inference Provider

**Decision**: Azure AI Foundry endpoints, proxied through the .NET 10 backend via `Azure.AI.Inference` SDK.

**Rationale**: User explicitly specified Azure AI Foundry. The backend acts as a secure proxy: the client never holds API credentials. Credentials are pulled from Azure Key Vault (Constitution § V). The proxy pattern also allows token counting and timing to be instrumented server-side for telemetry accuracy.

**Token cost calculation**: Azure AI Foundry exposes per-token pricing metadata per deployment. The backend reads this at startup and stores it in configuration; cost = `prompt_tokens * input_price + completion_tokens * output_price`.

**Alternatives considered**: Direct client-side OpenAI/Anthropic calls — rejected because it would expose API keys in the browser and violates Constitution § V (no secrets in client).

---

## 3. Real-Time Processing-Phase Updates

**Decision**: SignalR hub (`/hubs/duel`) for streaming per-model status, token count, and elapsed time from server to client during the processing phase. REST POST for final verdict persistence.

**Rationale**: The user's stack document explicitly mentions SignalR for real-time telemetry. The processing HUD (status label, token count, time-remaining) maps naturally to server-push events from the SignalR hub. The local WebLLM worker reports its own token events to the client, which relays aggregate stats to the server hub for unified tracking.

**Message cadence**: Hub broadcasts every 500ms per model; client updates HUD on each message without re-rendering the full component (only the stats fields).

**Alternatives considered**: Polling — rejected because 500ms polling generates excessive HTTP traffic during a 5-minute window. WebSockets raw — rejected in favour of SignalR's typed hub abstraction and automatic reconnect.

---

## 4. Energy / "Green Stats" Calculation

**Decision**: TDP Profile Engine on the server. Active generation time × configured TDP (default: 115 W for RTX 5070 Ti Mobile) = Wh estimate. Financial cost = Wh / 1000 × cost-per-kWh (configurable, default: $0.15/kWh USD).

**Rationale**: Browser sandboxing prohibits direct GPU watt-sensor access. User explicitly specified the TDP Profile Engine approach correlated against the 5070 Ti's 115W TGP. Both TDP and electricity rate are `appsettings` values (Constitution § IX feature flags) overridable without redeployment.

**Green Score formula**: `Green Score = output_tokens / energy_Wh` (tokens per watt-hour). Higher = more efficient. Used for Leaderboard "Green Score" sort (FR-026).

**Alternatives considered**: Browser Performance API timestamps — insufficient; provides timing but not power draw. Hardware counter APIs — not available in browser sandboxes.

---

## 5. Data Persistence (Azure Table Storage)

**Decision**: Azure Table Storage via `Azure.Data.Tables` SDK. Local development uses Azurite in Docker. Three tables:
- `Models` — PartitionKey: `"model"`, RowKey: `modelId`. Stores display name, type, current ELO, TDP watts, API endpoint reference.
- `Duels` — PartitionKey: `YYYYMM` (month bucket for efficient range queries), RowKey: `duelId` (ULID). Stores prompt, model IDs, verdict, timestamps.
- `DuelResults` — PartitionKey: `duelId`, RowKey: `modelId`. Stores full telemetry per model per duel.
- `EloHistory` — PartitionKey: `modelId`, RowKey: `timestamp + duelId`. Immutable ELO snapshots for sparklines (last 20 fetched by top-20 query ordered by RowKey desc).

**Rationale**: Constitution § V mandates Azure Table Storage in the app's own resource group. Schema-less nature accommodates future telemetry fields without migrations. ULID RowKeys for Duels provide time-ordering without a sort query.

**Alternatives considered**: Azure SQL — rejected; relational schema unnecessary for this append-heavy, simple-query workload. Cosmos DB — rejected; more complex than needed and higher cost.

---

## 6. ELO Engine

**Decision**: Standard Elo formula implemented as a pure Domain service (`EloCalculator`) with configurable K-factor (default: 32). 

```
E_a = 1 / (1 + 10^((R_b - R_a) / 400))
R'_a = R_a + K * (S_a - E_a)
```

Where `S_a = 1.0` (win), `0.0` (loss). Draws are not possible (user must pick a winner). Results displayed to 1 decimal place minimum per spec edge case.

**K-factor**: Stored in `appsettings.json` as `Elo:KFactor` (default 32); overridable without redeployment (Constitution § IX feature flags).

**Alternatives considered**: Glicko-2 — rejected; requires rating deviation tracking and is unnecessarily complex for a single-player benchmarking tool. TrueSkill — Microsoft licence constraints; overkill for 1v1 format.

---

## 7. Lab Report Export

**Decision**: Server-side Razor-to-HTML rendering. A dedicated `/api/duels/{id}/report` endpoint renders a Razor view to a self-contained HTML string, inlining all CSS and base64-encoding any assets. The response is returned with `Content-Disposition: attachment; filename="lab-report-{duelId}.html"`.

**Rationale**: User's stack explicitly specifies "Razor-to-HTML engine on the server." Self-contained = no external requests when opened offline (SC-005). All telemetry, ELO data, and both model HTML outputs are embedded inline.

**Security**: Generated HTML from models is sanitised before embedding in the report (XSS risk — model output is untrusted). Sanitisation removes `<script>` tags and `on*` attributes from the model output sections of the report; the sandboxed viewport sections use `srcdoc` isolation.

**Alternatives considered**: Client-side Blob export — rejected; cannot guarantee self-contained inline CSS without server coordination. PDF — rejected; spec explicitly says HTML format.

---

## 8. Audio System

**Decision**: Web Audio API with `.wav` assets embedded as resources in the Blazor WASM assembly.

**Rationale**: User explicitly specified Web Audio API with embedded `.wav` for zero-latency triggering. Pre-loaded `AudioBuffer` objects ensure the snare-roll fires the instant "Commence Duel" is clicked without network fetch delay.

**Graceful degradation**: If `AudioContext` is unavailable or autoplay policy blocks sound (Constitution § VII — audio unavailable), the application proceeds silently with no error surfaced.

---

## 9. Architecture Tension: Onion vs. User's VSA Proposal

**Decision**: **Onion Architecture as mandated by Constitution § II.** The user's technical stack document mentioned VSA (Vertical Slice Architecture), but the Constitution explicitly and unambiguously requires Onion Architecture with physically separate assemblies for Domain, Application, and Infrastructure. The Constitution supersedes all other inputs.

**Resolution**: VSA's "feature folder" organisation can be applied *within* the Application layer (grouping use cases by feature slice) without violating Onion's dependency rules. This gives the organisational benefits of VSA while preserving the strict dependency inversion the Constitution requires.

**Complexity Tracking entry required**: None — Onion is the constitutional default; no justification needed.

---

## 10. Sandbox Security for Rendered Model Output

**Decision**: `<iframe srcdoc="...">` with `sandbox="allow-scripts allow-same-origin"` removed (use `sandbox` attribute alone — no flags). This allows scripts within the generated HTML to run (needed for demos with JS) while preventing access to parent page DOM, localStorage, or cross-origin requests.

**Rationale**: Model-generated HTML is untrusted. An iframe with no `allow-same-origin` flag means the sandboxed content cannot access parent-origin storage or cookies. `allow-scripts` is needed to run the JS in generated apps. This is the minimum viable sandboxing for an interactive viewport.

**CSP**: Server responses include `Content-Security-Policy: frame-ancestors 'self'` to prevent clickjacking on the Arena page.

---

## 11. Watchdog Timer Implementation

**Decision**: Server-side `CancellationTokenSource` with a 300-second timeout per model task. Local model watchdog is additionally enforced client-side in the Web Worker (`setTimeout` → `worker.terminate()`). Whichever fires first wins; partial output captured to that point is persisted.

**Rationale**: Server-side cancellation handles remote model timeout reliably. Client-side Web Worker termination handles local model timeout without requiring a server round-trip. Dual enforcement ensures the timer is never circumvented by network lag.

---

## 12. Responsive Layout Breakpoints

**Decision**: Desktop (≥1024px): side-by-side viewports in a CSS Grid 2-column layout. Mobile (<1024px): vertical stack with CSS `scroll-snap` for swipeable behaviour. The 1024px threshold aligns with Blazor's common breakpoint conventions and covers the Pixel 9 Pro's landscape width.

**Alternatives considered**: Component library breakpoints (Radzen's `<RadzenSplitter>`) — usable for the desktop layout but not for mobile swipe; CSS scroll-snap retained for mobile.

---

## All NEEDS CLARIFICATION items resolved ✅

| Item | Resolution |
|------|-----------|
| Local model runtime | WebLLM / WebGPU / Web Worker |
| Remote model provider | Azure AI Foundry via .NET proxy |
| Persistence layer | Azure Table Storage (4 tables defined) |
| Real-time updates | SignalR hub `/hubs/duel` |
| Energy calculation | TDP Profile Engine (115W default, configurable) |
| ELO engine | Standard Elo, K=32, pure Domain service |
| Lab Report format | Server-side Razor-to-HTML, self-contained |
| Audio system | Web Audio API, embedded `.wav` assets |
| Architecture | Onion (Constitution § II) with VSA-style feature folders in Application layer |
| Sandbox security | `<iframe srcdoc>` with restricted `sandbox` attribute |
| Watchdog | Dual: server `CancellationTokenSource` + client Web Worker `terminate()` |
| Responsive layout | CSS Grid (desktop) + CSS scroll-snap (mobile), 1024px breakpoint |
