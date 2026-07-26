# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Read first

[AGENT.MD](AGENT.MD) is the living architectural contract — tech stack, project structure, config keys,
deployment, and testing strategy. [docs/PRD_Master.md](docs/PRD_Master.md) is the source of truth for
slice boundaries, the endpoint map, the Table Storage schema, and the decision log (§9). Keep both
current when you change architecture; the decision log is where deviations get recorded.

This repo is governed by the user's global **NET_RULES** ruleset for all `Po*` .NET solutions
(`Po` prefix everywhere, .NET 10, CPM, VSA, BFF auth, master-only branching, Azure App Service +
Table Storage). AGENT.MD documents where this repo deliberately deviates — check it before assuming
a rule is being violated.

## Commands

```powershell
pwsh SCRIPTS/setup.ps1                                          # first-run machine setup (winget, Docker/Azurite, ports)
docker compose up -d azurite                                    # storage only
dotnet run --project src/PoLocalCompare.Api --launch-profile https   # app at https://localhost:5001
dotnet build PoLocalCompare.slnx                                # whole solution
```

Ports **5000/5001 are fixed** — never change them without explicit instruction. The API hosts the
Blazor WASM client, so there is one process, not two.

### Tests

Two projects, each with slices in folders. Unit tests need nothing; everything else needs Docker
(Testcontainers spins Azurite) and the UI slice needs a running app.

```powershell
dotnet test tests/PoLocalCompare.Tests                                    # unit + integration
dotnet test tests/PoLocalCompare.Tests --filter FullyQualifiedName~Unit   # unit only, no Docker
dotnet test tests/PoLocalCompare.Tests.E2E --filter Category!=UI          # API journeys
dotnet test tests/PoLocalCompare.Tests.E2E --filter Category=UI           # Playwright (app must be running)
dotnet test tests/PoLocalCompare.Tests --filter FullyQualifiedName~EloCalculatorTests.Calculate_WinForA_AIncreasesAndBDecreases
```

UI tests run **headed Chrome by default** across a mobile and a desktop viewport; set `HEADLESS=1`
to suppress windows and `BASE_URL` to point elsewhere. Browsers install via
`pwsh tests/PoLocalCompare.Tests.E2E/bin/Debug/net10.0/playwright.ps1 install chromium`.

Tests are **never run in CI** — [.github/workflows/deploy.yml](.github/workflows/deploy.yml) only
publishes and deploys. That makes the E2E suites easy to leave stale; run them locally after touching
UI markup or the HTTP surface.

## Architecture

**One host, three inference paths, asymmetric execution.** This is the thing that requires reading
several files to see. A duel pits two models against each other, but *where* inference runs differs:

- **Remote** (Azure AI Foundry) and **Ollama** models execute server-side. `DuelExecutionService`
  queues work on `IBackgroundTaskQueue`, resolves an `IRemoteInferenceProxy` (Strategy, per model
  type), and streams tokens out over the `DuelHub` SignalR hub.
- **Browser** (WebLLM/WebGPU) models execute *in the client*, in a web worker
  (`wwwroot/js/webllm-worker.js` behind `WebLlmService`). The server never sees that inference — the
  client POSTs the finished output to `POST /api/duels/{id}/local-result`.

So a single duel can be half server-orchestrated and half client-orchestrated, converging in Table
Storage. When changing duel flow, check both paths; a change that only touches
`DuelExecutionService` silently misses browser models.

**Auth is BFF.** The API owns the OIDC code flow and the session; the WASM client holds no tokens,
only an `HttpOnly`/`SameSite=Strict` cookie. Server authorization is deny-by-default
(`FallbackPolicy = RequireAuthenticatedUser`) — new public endpoints must opt out with
`.AllowAnonymous()`. Dev/test use a `FakeAuthHandler` driven by `X-Fake-User`/`X-Fake-Roles` headers
that **throws if constructed in Production**; integration and E2E-API tests depend on it, which is why
their host runs as `Development`.

**Verdicts are human-only.** Nothing auto-awards a duel — not even when one model fails and the other
succeeded. ELO moves solely through `RecordVerdictHandler` on a human decision; the Arena offers a
**Retry duel** action instead of resolving a failure automatically.

**Vertical slices.** Server code lives in `src/PoLocalCompare.Api/Features/<Feature>/` — endpoint,
handlers, entities, and repository flat in one folder. `Common/` is only for genuinely cross-slice
code. There is no Domain/Application/Infrastructure split; it was collapsed in 2026-07-06 (PRD §9).

**Persistence details that bite.** Table Storage writes are idempotent and ETag-safe: creates swallow
409, updates are If-Match conditional, and duel writers re-read and reapply on 412. `HybridCache`
(30s TTL, tag-invalidated on verdict) fronts leaderboard and model-availability reads — invalidate it
when you add a write path that affects those. Typed HttpClients use **retry-only** resilience
pipelines; adding a per-attempt timeout will abort SSE streams.

## Constraints worth knowing before you edit

- `TreatWarningsAsErrors` is global — a new warning fails the build.
- **No AOT.** Never set `RunAOTCompilation=true`.
- `LangVersion=latest` (standards mandate C# 15; SDK 10 tops out at 14 and rejects an explicit `15`).
- Work on `master`; no feature branches unless asked.
- `/health` and `/diag` exist but must have **no UI links**. `/diag` masks secret values.
- When `Features:UseRealAi` is off, the `USING MOCK DATA` banner must render (`NavMenu.razor`).
- UI targets **WCAG 2.2 Level AA**: keyboard-operable custom controls, `:focus-visible` ring, 24×24
  minimum target size, `role="status"` for live updates, `aria-hidden` on decorative glyphs. Colour
  contrast is not automatically checked — verify new palette tokens by hand.
- Styling should be scoped `.razor.css` with design tokens. Known debt: inline `style=` attributes and
  `<style>` blocks remain in `ModelCard.razor`, `SandboxedViewport.razor`, and `Leaderboard.razor`.

## Known stale documentation

[README.md](README.md) describes a "GPT-4.1 Nano auto-judges after the 24-hour deadline" feature. That
service was removed, and so was the later forfeit auto-award: **a human judge decides every duel**,
including one where a model failed. ELO only ever moves on a human verdict. Don't build on the
auto-judge description.
