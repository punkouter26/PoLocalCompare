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

There is no linter or formatter step — `TreatWarningsAsErrors` is the whole gate. (`validate-standards.ps1`
used to sit here and was deleted in the 2026-08-13 prune: it still expected the pre-VSA `src/Client/...`
layout and failed on a healthy tree. Don't reintroduce it.)

### Tests

Four projects, one per tier. Unit needs nothing; Integration and E2EAPI need Docker
(Testcontainers spins Azurite) and E2EUI needs a running app.

```powershell
dotnet test tests/PoLocalCompare.Unit          # pure logic, no Docker
dotnet test tests/PoLocalCompare.Integration   # Testcontainers Azurite
dotnet test tests/PoLocalCompare.E2EAPI        # API journeys
dotnet test tests/PoLocalCompare.E2EUI         # Playwright (app must be running)
dotnet test tests/PoLocalCompare.Unit --filter FullyQualifiedName~EloCalculatorTests
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

**`web-llm.js` is a Git LFS object.** The vendored bundle is 6.5 MB — larger than all the source
in the repo combined — so it is tracked through LFS rather than as an ordinary blob. A clone made
without `git lfs install` gets a ~130-byte pointer file instead, and the symptom is every browser
model failing at `import` time in `webllm-worker.js` while everything else works normally. The
`build` job in [deploy.yml](.github/workflows/deploy.yml) and the `Fetch WebLLM artifacts`
workflow both check out with `lfs: true` — the latter because `plan-webllm-artifacts.py` parses
`web-llm.js` for the model list, so a pointer file breaks the catalog check too.

**Browser weights are optional but bimodal.** With `wwwroot/models/` absent, WebLLM pulls weights from
the CDN *and* `model_lib` `.wasm` files from raw.githubusercontent.com — two separate hosts, either of
which a filtered network can block. `download-models.py` vendors both (weights per model dir, libs into
`wwwroot/models/_libs/`) and the worker prefers local. Half-populating it is the failure mode to watch for.

**Editing `webllm-worker.js` requires bumping the cache-buster.** It is loaded as
`new Worker('/js/webllm-worker.js?v=N')` from `webllm-interop.js`; without incrementing `N` the browser
serves the old worker and your change appears to do nothing. The same trap applies to every
`<script src="/js/*.js?v=N">` in [index.html](src/PoLocalCompare.Client/wwwroot/index.html) —
`theme.js`, `util.js`, `diag-interop.js` and `compare.js` all carry their own `v=`.

**Browser-side logic belongs in `PoLocalCompare.Shared`, not in the Client project.**
`PoLocalCompare.Unit` references only the Api project, so anything pure that lives under
`src/PoLocalCompare.Client/` cannot be reached by any tier except E2E-UI — the one suite CI never
runs. That is why the diff engine, the HTML analyzer, the prompt library and the demo planner sit in
`Shared/Analysis`, `Shared/Prompts` and `Shared/Demo` rather than beside the components that use
them. Razor components stay thin wrappers over those statics.

**Home is a flat form, not a wizard.** It was a three-panel disclosure accordion with a numbered
stepper, step-advance rules and a sticky readiness bar that existed only because the Compare button
could be collapsed out of view. The page is "two models, a prompt, a button" and now shows all of
it at once (`home__section` / `home__compare`). Don't reintroduce `home__panel*` — the E2E-UI
selectors point at the flat markup.

**The Arena is the whole duel — streaming and judging.** `/processing` no longer exists; `POST
/api/duels` navigates straight to `/arena/{id}`, which connects to `DuelHub`, shows the live
`TokenRace` and streaming previews while `_duelStillRunning`, then swaps to the verdict UI on
`DuelComplete`. Critically, **Arena drives browser-model inference**: it handles
`OnStartLocalInference`, runs `WebLlmService`, and POSTs to `/api/duels/{id}/local-result`. A
change that breaks that handler stalls every WebGPU pairing at `Initializing` with no error.

**There is no UI component library, and `.po-btn` is the only button.** Radzen was removed; buttons
and tables are `.po-btn` and `.po-table` in
[app.css](src/PoLocalCompare.Client/wwwroot/css/app.css), styled from design tokens. Twelve
per-surface button classes (`wizard__btn`, `demo__btn`, `h2h__btn`, `lab__btn`, `source-compare__btn`
…) had each reimplemented the same thing locally and drifted apart; they were folded into `.po-btn`
plus modifiers (`--sm --lg --block --primary --success --secondary --ghost --warn --danger`). A
surface that needs a tweak adds a **layout-only** class alongside `.po-btn` — `arena__action-btn`,
`archive__btn`, `leaderboard__sort-btn` and `lab-card__icon-btn` are the pattern. New *visual*
variants go in `app.css` as a modifier, never in a `.razor.css`. The two exceptions are deliberate:
`login__ms-btn` and `navmenu__ms-btn` restate a fixed white field because the Microsoft mark is
trademarked artwork with a mandated presentation. Note also the app has no reflective component
instantiation except the Router's `NotFoundPage`, which is the only remaining reason
`PublishTrimmed` is off.

**Every surface owns exactly one BEM block, named after its file.** `NavMenu` → `navmenu__`,
`Login` → `login__`, `Home` → `home__`, `LabModelCard` → `lab-card__`, `ModelHealthPanel` → `lab__`.
This is enforced by nothing, and it has broken twice: `Leaderboard.razor.css` carried both `lb__`
and `leaderboard__`, and `LabModelCard` shared `lab__` with its parent panel — which is exactly the
scope-id trap below waiting to happen. Do not introduce a second block into a stylesheet.

**Classes in markup with no rule anywhere are a recurring defect.** The nav bar carried
`nav-item`, `btn-sm` and `btn-outline-warning` long after Bootstrap was gone, and
`arena__source-btn`, `arena__generating-notice`, `auth-spinner`, `h2h__sparkline-col` and
`scorecard__findings-col` all styled nothing. Nothing warns. To check the whole app, diff the
classes used in `.razor` markup against the selectors defined in any `.css`.

**Scoped CSS is per-`.razor`-file, and nothing warns when it isn't.** `ModelHealthPanel.razor.css`
had spent a long time styling `LabModelCard`'s markup, which silently matched nothing because
Blazor stamps each stylesheet with its own component's scope id. If you move markup into a child
component, move its rules into that component's own `.razor.css` (or use `::deep` — which is why
`navmenu__link` rules need it, since `NavLink` renders the anchor outside the component's scope).
A class that is built by interpolation — `lab-card__vram-badge--@State.VramTier`,
`leaderboard__type-badge--@ModelTypeGroup.CssModifier(t)` — will also read as dead to any text
scan, so check for those before deleting a rule.

**Client code that isn't a component doesn't live in `Components/`.**
`src/PoLocalCompare.Shared/Presentation/` holds the view-models, enums and static helpers that
`.razor` files lean on (`ModelDiagState`, `ModelTypeGroup`, `SourceViewMode`, `RuntimeProbeReport`,
`FailureReasonText`, `RenderCoalescer`). It used to be `Client/Presentation/`, which put it in the
one assembly no tier but E2E-UI can reach — the same trap as the note above, so it moved wholesale.
`src/PoLocalCompare.Client/Services/` keeps what genuinely needs the browser: JS interop, the
SignalR client, and `LocalInferenceDriver` (which runs a WebGPU model in the tab and POSTs the
result back — extracted out of `Arena.razor`, which was the only thing that knew how).

**The Arena's scorecard must never feed ELO.** `OutputAnalysis.CompletenessScore` is presentational
and deliberately separate from the persisted `OutputQualityScore` — tightening a heuristic there must
not retroactively change a stored duel. Likewise, the runtime probe injects a reporter `<script>` into
the sandboxed *preview* only; the raw output is what gets persisted, analysed, diffed and shown by
"View Source", so nothing a person judges or exports contains it.

**Demo mode writes real duels.** `/demo` runs ten remote-vs-remote duels that persist, get judged and
move ELO, using `POST /api/duels` with `autoJudgeDelaySeconds: 0`. It is not a sandbox — if you need
a throwaway run, wipe Azurite afterwards. The override is clamped 0–3600 and cannot switch the judge
on: `AiJudge:Enabled=false` still restores human-only verdicts.

**Vertical slices.** Server code lives in `src/PoLocalCompare.Api/Features/<Feature>/` — endpoint,
handlers, entities, and repository flat in one folder. `Common/` is only for genuinely cross-slice
code — `Common/Domain/` was dissolved in the 2026-08-13 prune because all four of its calculators
had exactly one consuming slice (`GreenStatsCalculator`, `HtmlOutputNormalizer` and
`HtmlOutputQualityScorer` to `Features/Duels`, `WinRateCalculator` to `Features/Leaderboard`). There is no Domain/Application/Infrastructure split; it was collapsed in 2026-07-06 (PRD §9).

**Streaming re-renders are coalesced, and the frames opt out.** Token-batch updates arrive far
faster than a frame. `Arena` and `Demo` funnel their per-batch handlers through
`RenderCoalescer.Request()` (~16 ms trailing edge) instead of calling `StateHasChanged` directly,
and `SandboxedViewport` implements `ShouldRender` gated on an ordinal compare of its raw HTML —
without that, every render re-emitted `srcdoc` and the browser tore down and reloaded the preview
mid-generation. Terminal events (`DuelComplete`, verdicts) still paint immediately; don't route
those through the coalescer.

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
