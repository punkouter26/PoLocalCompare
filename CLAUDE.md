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
docker compose down -v; docker compose up -d azurite            # wipe local tables (the only way to force a re-seed)
python SCRIPTS/download-models.py                               # vendor browser-model assets (~5 GB; MODELS=small ≈ 1 GB)
```

Ports **5000/5001 are fixed** — never change them without explicit instruction. The API hosts the
Blazor WASM client, so there is one process, not two.

There is no linter or formatter step — `TreatWarningsAsErrors` is the whole gate.
`SCRIPTS/validate-standards.ps1` **does not work**: it still expects the pre-VSA `src/Client/...`
layout and a root `architecture.md` that no longer exists, so it fails on a healthy tree. Nothing
runs it — don't treat its output as signal, and don't "fix" the repo to satisfy it.

### Tests

Four projects, one per tier. Unit needs nothing; Integration and E2EAPI need Docker
(Testcontainers spins Azurite) and E2EUI needs a running app.

```powershell
dotnet test tests/PoLocalCompare.Unit          # pure logic, no Docker
dotnet test tests/PoLocalCompare.Integration   # Testcontainers Azurite
dotnet test tests/PoLocalCompare.E2EAPI        # API journeys
dotnet test tests/PoLocalCompare.E2EUI         # Playwright (app must be running)
dotnet test tests/PoLocalCompare.Unit --filter FullyQualifiedName~EloCalculatorTests.Calculate_WinForA_AIncreasesAndBDecreases
```

UI tests run **headed Chrome by default** across a mobile and a desktop viewport; set `HEADLESS=1`
to suppress windows and `BASE_URL` to point elsewhere. Browsers install via
`pwsh tests/PoLocalCompare.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium`.

Unit, Integration and E2EAPI **gate the deploy** — [.github/workflows/deploy.yml](.github/workflows/deploy.yml)
runs them in a `test` job that `build` depends on. **E2EUI is not in CI** (real headed Chrome, WebGPU
paths a runner has no GPU for), so it is the one suite that goes stale silently; run it locally after
touching UI markup.

AGENT.MD §8 fixes a **ratio contract of 100 / 50 / 25 / 25** — integration ≈ half of unit, each E2E
tier ≈ a quarter. Counts are test *cases*, so a `[Theory]` contributes one per `InlineData` and each
UI method counts twice (two viewports). Keep new tests inside those proportions rather than piling
onto whichever tier is easiest to write.

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

**Verdicts are human-first, then auto-judged.** A human who picks a winner in the Arena within
`AiJudge:DelaySeconds` of the duel finishing always decides it. Otherwise `AutoJudge` asks a Foundry
model which output follows the prompt better and records that verdict itself. ELO still moves only
through `RecordVerdictHandler`, but it now has two callers, so **every verdict carries a
`VerdictSource`** (`Human` or `Ai`) — never add a write path that moves ELO without setting it, or
the leaderboard silently blends two different signals with no way to separate them afterwards.

Three invariants hold the design together. A human decision always wins the race (`AutoJudge`
re-reads the duel and stands down on anything but `Pending`, and `RecordVerdictHandler` throws on a
second verdict). A judge that cannot decide — unreachable, unparseable reply, or both models failed —
leaves the duel `Pending` rather than guessing; ELO must never move on no evidence. And
`AiJudge:Enabled=false` genuinely restores the old human-only behaviour.

This reverses the original human-only rule; PRD §9 item 7 records why it was that way and item 9 why
it changed. Note the default 5-second window is too short to read two outputs, so in practice the
judge decides nearly every duel — widen `DelaySeconds` if the human path needs to be usable.
The Arena still offers **Retry duel** for transient failures.

**The model catalog is spread across three files that must agree.**
[ModelSeeder.cs](src/PoLocalCompare.Api/Features/Models/ModelSeeder.cs) is the catalog, but it seeds
**only when the Models table is completely empty** — editing it changes nothing on a machine that has
already run, so wipe Azurite (`docker compose down -v`) or the new entry never appears. Browser models
additionally need a matching `prebuiltAppConfig` entry in
[web-llm.js](src/PoLocalCompare.Client/wwwroot/js/web-llm.js);
`SCRIPTS/plan-webllm-artifacts.py` parses both files, is the single source of the model list for the
local downloader *and* the `Fetch WebLLM artifacts` workflow, and exits non-zero when they disagree —
run it after any catalog edit. Retired seed IDs are commented out, never reused (007/008 are burnt).
Ollama (`ModelType.LocalService`) models seed in **Development only**, so Production has no dead entries.

**Browser weights are optional but bimodal.** With `wwwroot/models/` absent, WebLLM pulls weights from
the CDN *and* `model_lib` `.wasm` files from raw.githubusercontent.com — two separate hosts, either of
which a filtered network can block. `download-models.py` vendors both (weights per model dir, libs into
`wwwroot/models/_libs/`) and the worker prefers local. Half-populating it is the failure mode to watch for.

**Editing `webllm-worker.js` requires bumping the cache-buster.** It is loaded as
`new Worker('/js/webllm-worker.js?v=N')` from `webllm-interop.js`; without incrementing `N` the browser
serves the old worker and your change appears to do nothing.

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
- A new `Features/<Feature>/` or `Common/<Area>/` folder needs its namespace added to
  [GlobalUsings.cs](src/PoLocalCompare.Api/GlobalUsings.cs); slices reference each other with no
  per-file `using`, so omitting it produces confusing "type not found" errors elsewhere.
- Packages are centrally managed — versions go in `Directory.Packages.props`, never in a `.csproj`.
- **No AOT.** Never set `RunAOTCompilation=true`.
- `LangVersion=latest` (standards mandate C# 15; SDK 10 tops out at 14 and rejects an explicit `15`).
- Work on `master`; no feature branches unless asked.
- `/health` and `/diag` exist but must have **no UI links**. `/diag` masks secret values.
- When `Features:UseRealAi` is off, the `USING MOCK DATA` banner must render (`NavMenu.razor`).
- UI targets **WCAG 2.2 Level AA**: keyboard-operable custom controls, `:focus-visible` ring, 24×24
  minimum target size, `role="status"` for live updates, `aria-hidden` on decorative glyphs. Colour
  contrast is not automatically checked — verify new palette tokens by hand.
- Styling is scoped `.razor.css` + design tokens; there are **no** inline `style=` attributes or
  `<style>` blocks left. A genuinely dynamic value (a progress width, an animation stagger) is passed
  as a CSS custom property — `style="--fill: 42%"` — and consumed by a rule in the stylesheet, so the
  styling itself stays in CSS. Colour tokens are declared for light, for `prefers-color-scheme: dark`,
  and again under `:root[data-theme=...]`; the `[data-theme]` blocks must stay last or the header's
  theme toggle cannot override the OS preference.

## Known stale documentation

The original "GPT-4.1 Nano auto-judges after the 24-hour deadline" service is gone, and so is the
forfeit auto-award that replaced it. Auto-judging now exists again but works differently from both:
the trigger is a short grace window after the duel finishes (`AiJudge:DelaySeconds`), not a 24-hour
deadline, and the judge is whatever `AiJudge:Deployment` names. See the verdict section above.
