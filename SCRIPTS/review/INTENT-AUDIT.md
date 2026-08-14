# Intent Audit (Review #8)

**Method:** for every server feature and every Razor page, write one sentence: *"This exists because ___."* If the answer doesn't defend itself, the feature is a prune candidate. PRD §9 items 16-19 each removed whole slices on the same test.

> Verdict legend: **Keep** — defended; **Prune?** — answer is weak; **Already pruned** — listed for awareness.

## Server slices (PRD §2, endpoint map §3)

| Slice / route | One-sentence justification | Verdict |
|---|---|---|
| `GET /health` | External probes (Azure App Service health check, status pages) need a ping that doesn't require auth and reports per-dependency status. | **Keep** |
| `GET /api/diag/smoke` | Dev/ops surface that reports Table Storage latency, Foundry reachability, Ollama status — replaces manual log-spelunking. | **Keep** (PRD §8) |
| `GET /api/diag/warnings` | Companion to smoke; surfaces pre-deploy warnings. | **Keep** |
| `/diag` Razor | Human-readable companion to the JSON endpoints; carries secret-mask guarantee (PRD §8). | **Keep** — but no UI link (PRD §8); verify |
| `POST /api/dev/reset` | Wipe local Azurite between demos. | **Keep** (dev-only) |
| `POST /api/dev/remap-model-ids` | Run `OrphanModelIdRemapper` by hand after a wipe. | **Keep** (dev-only) |
| `/scalar`, `/openapi` | API explorer for the operator; dev-only. | **Keep** |
| `GET /auth/me` | SPA needs to read its own auth state without holding a token. | **Keep** (PRD §7) |
| `GET /auth/login/microsoft`, `/auth/login/fake`, `POST /auth/logout` | BFF OIDC + dev guest bypass. | **Keep** (PRD §4.4, §7) |
| `GET /api/models` | List registered models with denormalized Elo/W-L for selection. | **Keep** |
| `GET /api/models/availability` | Per-model runtime probe (Ollama, Foundry); drives the "N models can't run here" toggle on Home. | **Keep** |
| `POST /api/models` | Register a new model in the catalog (e.g., add a Foundry deployment). | **Keep** (used by Arena's Re-Challenge flow when pairing an unseen model — re) |
| `PATCH /api/models/{id}` | — | **Already pruned** (PRD §9 item 16). |
| `DELETE /api/models/{id}` | — | **Prune candidate** — no UI calls it, no test calls it. The catalog is seeded. |
| `POST /api/models/{webLlmModelId}/download` | Vendored-weights downloader for browser models. | **Keep** |
| `GET /api/models/download-status/{id}` | — | **Already pruned** (PRD §9 item 16 — `checkModelFile` probe replaced it). |
| `POST /api/duels` | Create + queue a duel. | **Keep** |
| `GET /api/duels` | Archive listing with paging. | **Keep** |
| `GET /api/duels/demo-plan` | Pure read; resolves the pairing/prompt schedule for `/demo`. | **Keep** |
| `GET /api/duels/{id}` | Single duel telemetry DTO. | **Keep** |
| `POST /api/duels/{id}/local-result` | Browser-model inference result ingest. | **Keep** (PRD §1: one of three inference paths converges here) |
| `POST /api/duels/{id}/verdict` | Record human/AI verdict; moves Elo. | **Keep** (single chokepoint per PRD §9 item 18) |
| `GET /api/duels/{id}/report` | Self-contained HTML Lab Report. | **Keep** — used by Archive + Arena's export disclosure |
| `GET /api/leaderboard` | Cached leaderboard DTO; tag-invalidated on verdict. | **Keep** |
| `GET /api/leaderboard/{id}/killlist` | Per-opponent W/L/T aggregates. | **Keep** — surface that drove the `/h2h` deletion (PRD §9 item 19) |
| `GET /api/ollama/*` (gpu-status, available-models, /benchmark) | Local-only diagnostics — only meaningful when Ollama is running. | **Prune candidate** for `/benchmark` — never called from UI. Keep the other two (drives the lab panel). |
| `GET /health` for Foundry endpoint probe | Healthy when not configured (mock mode). | **Keep** |

## Razor pages (PRD §11)

| Page | One-sentence justification | Verdict |
|---|---|---|
| `/` (Home) | Compare two models + prompt in three disclosures, then start the duel. | **Keep** |
| `/arena/{id` | Stream a running duel; render both outputs; carry the verdict decision. | **Keep** (single page that does streaming + judging + browser-model inference) |
| `/leaderboard` | ELO rankings + per-row kill list + sort by Quality/Cost. | **Keep** |
| `/archive` | Browse every duel ever run; download lab report; jump to Arena. | **Keep** |
| `/demo` | Run 10 unattended remote-vs-remote duels, state the ELO impact up front. | **Keep** |
| `/login` | Microsoft + (dev-only) guest sign-in. | **Keep** |
| `/notfound` | Router catch-all 404. | **Keep** |

## Components

| Component | One-sentence justification | Verdict |
|---|---|---|
| `ModelCard.razor` | Single selectable card; shows ELO/W-L/Params/cost. | **Keep** |
| `ModelHealthPanel.razor` | Per-model runtime probe on Home; reveals Ollama / Foundry reachability. | **Keep** |
| `LabModelCard.razor` | Health-panel sub-card for one model. | **Keep** — but it's only used inside ModelHealthPanel (clause 4.5 of CLAUDE.md: BEM block collides with parent at `lab__` → `lab-card__`) — verify |
| `PromptPicker.razor` | Curated starter + recent prompts. | **Keep** |
| `TokenRace.razor` | Live token-race visualisation during the duel. | **Keep** |
| `SandboxedViewport.razor` | Sandboxed iframe for the model output (sandbox="allow-scripts", opaque origin). | **Keep** |
| `TelemetryHud.razor` | Per-side metrics (tok/s, ms, tokens, bytes, $). | **Keep** |
| `OutputScorecard.razor` | Structural score; deliberately separate from persisted `OutputQualityScore` per PRD §9 item 13. | **Keep** |
| `SourceCompare.razor` | Rendered/Code/Diff view switch. | **Keep** |
| `EloSparkline.razor` | Per-model ELO trend. | **Keep** |

## Candidates for next prune

Ordered by lowest cost-of-removal first:

1. **`DELETE /api/models/{id}`** — no UI, no test. **Cost:** ~15 min. **Risk:** none. **Confirms:** PRD §9 item 16 already killed `PATCH /api/models/{id}` for the same reason.
2. **`POST /api/ollama/benchmark`** — UI surfaces the other two Ollama endpoints only. **Cost:** ~30 min. **Risk:** none observed. **Confirms:** PRD §9 item 16.
3. **`/lab__env--ok` and `/lab__env--warn`** — defined in `ModelHealthPanel.razor.css` (audit found both), not applied in `ModelHealthPanel.razor` markup. Either restate to real selectors or delete. **Cost:** ~5 min. **Risk:** none.

The four "orphan" BEM classes reported by the audit but not surfaced above (`/arena__elo-badge--positive/negative`, `/home__panel--open`, `/arena__viewport-panel--failed`, etc.) are **conditional state classes applied via Razor expressions** — they are used, the static diff just doesn't see them. They belong in the audit's known-blind-spots list, not the prune list.

> Note: PRD §9 item 19 already pulled this pass once. The defensive value of an *external* review is to verify nothing has accreted since.