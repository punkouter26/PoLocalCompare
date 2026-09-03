# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Read first

[AGENT.MD](AGENT.MD) is the living architectural contract — tech stack, project structure, config keys,
deployment, and testing strategy. [docs/PRD_Master.md](docs/PRD_Master.md) is the source of truth for
slice boundaries, the endpoint map, the Table Storage schema, and the decision log (§9). Keep both
current when you change architecture; the decision log is where deviations get recorded.

If a **`DOCS/`** folder exists in the repository root, read it before making changes — it carries the
overall project summary. (`docs/PRD_Master.md` is referenced throughout this file but is **not in the
working tree** right now; if neither is present, say so rather than reconstructing the overview from
the code.)

This repo is governed by the user's global **NET_RULES** ruleset for all `Po*` .NET solutions
(`Po` prefix everywhere, .NET 10, CPM, VSA, BFF auth, master-only branching, Azure App Service +
Table Storage). AGENT.MD documents where this repo deliberately deviates — check it before assuming
a rule is being violated.

## Working rules

- **`master` only.** All work lands on `master`. Do not create, check out or push a feature branch
  unless the request explicitly asks for one.
- **Restart the app and verify it came up after every code change.** Stop the running process,
  `dotnet run --project src/PoLocalCompare.Api --launch-profile https`, and confirm the build
  succeeded, no startup exception was thrown, and `https://localhost:5001` responds — before calling
  the change done. Startup is where DI registration, options binding and Key Vault wiring actually
  fail; none of that shows up in a successful compile.
- **No `dotnet user-secrets`.** Local values go in `appsettings.Development.json` or an environment
  variable (`AzureAiFoundry__ApiKey`); anything genuinely secret goes in **Azure Key Vault**, already
  wired through `KeyVault:Uri`. The `UserSecretsId` still in
  [PoLocalCompare.Api.csproj](src/PoLocalCompare.Api/PoLocalCompare.Api.csproj) is legacy — don't add
  new values to that store. Secrets never go in code, logs or committed files.

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
through `RecordVerdictHandler`, but it now has three callers, so **every verdict carries a
`VerdictSource`** (`Human`, `Ai` or `Constraint`) — never add a write path that moves ELO without
setting it, or the leaderboard silently blends different signals with no way to separate them
afterwards. `Constraint` is a challenge-budget forfeit: nothing read the outputs, which is why it
is a third value rather than filed under `Ai`.

Three invariants hold the design together. A human decision always wins the race (`AutoJudge`
re-reads the duel and stands down on anything but `Pending`, and `RecordVerdictHandler` throws on a
second verdict). A judge that cannot decide — unreachable, unparseable reply, or both models failed —
leaves the duel `Pending` rather than guessing; ELO must never move on no evidence. And
`AiJudge:Enabled=false` genuinely restores the old human-only behaviour.

**The judge looks at rendered screenshots, not just source.** A duel asking for a rotating cube
was won by a document that drew a flat plane: nothing in the source says "this is a plane" — the
shape only exists once the projection maths has run — so a text-only judge has to simulate the
script in its head, and does it badly. `HtmlScreenshotRenderer` (Common/Rendering) renders each
output in headless Chromium at the same 320×180 the models were told to design for, and
`FoundryDuelJudge` attaches both PNGs as image content parts with an instruction to **believe the
screenshot over the source**. Three things about it are load-bearing: it is **off by default**
(`AiJudge:VisionEnabled`, on only in Development) because the Free-tier App Service has no browser
and no room for one; **either side failing to render drops both**, since judging one document by
its picture and the other by its source is not a fair comparison; and **every failure degrades to
source-only** rather than throwing, because a duel must never go unjudged because a screenshot did
not render. The renderer is a singleton (Chromium takes ~1s to launch) and blocks all network from
the rendered page — a generated page's dead CDN reference must not become an outbound request from
the server, nor eat the settle window in timeouts. The judge deployment must accept image input;
`AiJudge:Deployment` is `gpt-5.4-mini` for that reason as well as for accuracy.

This reverses the original human-only rule; PRD §9 item 7 records why it was that way and item 9 why
it changed. **`AiJudge:DelaySeconds` is 10** (PRD §9 item 21) — short on purpose, so a duel resolves
while you are still looking at it. At that width the judge decides nearly every duel and the human
path is the Arena's vote buttons during the countdown; widen it if you want verdicts to be genuinely
human-first. `AutoJudgeOptions` used to document a "30-second floor applied at validation time" that
never existed — there is no options validator here, and the only clamp is the endpoint's 0–3600 on
the per-duel override. The Arena still offers **Retry duel** for transient failures.

**The model catalog is spread across three files that must agree.**
[ModelSeeder.cs](src/PoLocalCompare.Api/Features/Models/ModelSeeder.cs) is the catalog, but it seeds
**only when the Models table is completely empty** — editing it changes nothing on a machine that has
already run, so wipe Azurite (`docker compose down -v`) or the new entry never appears. Browser models
additionally need a matching `prebuiltAppConfig` entry in
[web-llm.js](src/PoLocalCompare.Client/wwwroot/js/web-llm.js);
`SCRIPTS/plan-webllm-artifacts.py` parses both files, is the single source of the model list for
`download-models.py`, and exits non-zero when they disagree — run it after any catalog edit. Retired seed IDs are commented out, never reused (007/008 are burnt).
Ollama (`ModelType.LocalService`) models seed in **Development only**, so Production has no dead entries.

**`web-llm.js` is a Git LFS object.** The vendored bundle is 6.5 MB — larger than all the source
in the repo combined — so it is tracked through LFS rather than as an ordinary blob. A clone made
without `git lfs install` gets a ~130-byte pointer file instead, and the symptom is every browser
model failing at `import` time in `webllm-worker.js` while everything else works normally. The
`build` job in [deploy.yml](.github/workflows/deploy.yml) checks out with `lfs: true`.
`plan-webllm-artifacts.py` also parses `web-llm.js` for the model list, so a pointer file breaks
the catalog check as well as the app.

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
runs. That is why the diff engine, the HTML analyzer, the prompt library and the bracket planner
sit in `Shared/Analysis`, `Shared/Prompts` and `Shared/Tournaments` rather than beside the
components that use them. Razor components stay thin wrappers over those statics.

**Home is a two-column workbench, not a wizard and not one column.** It was a three-panel
disclosure accordion with a numbered stepper, step-advance rules and a sticky readiness bar that
existed only because the Compare button could be collapsed out of view. Flattening it removed all
three but left a single 880px column running header → models → prompt → Compare, so the button the
page exists for still sat below every card in the catalog. It is a grid now (`home__layout`): the
picker (`home__picker`) scrolls on the left, and a sticky console (`home__console`) carries the
slots, the prompt and `home__compare` on the right. Three details there are load-bearing and easy
to undo by accident. The console is `grid-row: 1 / span 2` while `home__header` takes only the left
column's first row — that is what lets it start level with the title, so Compare lands inside the
first viewport instead of below the fold. Only `home__console-scroll` scrolls; `home__console-foot`
is `flex: 0 0 auto`, so Compare never scrolls away. And the console needs its explicit
`box-sizing: border-box`: there is no global reset, so without it the padding is added to the
`calc(100vh - …)` height and the foot hangs below the fold again. Below 1080px it collapses to one
column in the original reading order. Don't reintroduce `home__panel*` or `home__section` — the
E2E-UI selectors point at `home__title`, `home__grid` and `home__compare .po-btn`.

**The Arena is the whole duel — streaming and judging.** `/processing` no longer exists; `POST
/api/duels` navigates straight to `/arena/{id}`, which connects to `DuelHub`, shows the live
`TokenRace` and streaming previews while `_duelStillRunning`, then swaps to the verdict UI on
`DuelComplete`. Critically, **Arena drives browser-model inference**: it handles
`OnStartLocalInference`, runs `WebLlmService`, and POSTs to `/api/duels/{id}/local-result`. A
change that breaks that handler stalls every WebGPU pairing at `Initializing` with no error.

**There is a design-token scale, and raw values are the defect.** `app.css` defines
`--text-2xs…--text-4xl` (9 steps), `--space-2xs…--space-2xl` (9 steps), `--leading-*`,
`--weight-*` and `--radius-*`. Every `font-size` and every padding/gap/margin in the app goes
through them — there are **zero** raw `rem` values left in either. Before the 2026-08-22 pass
there were 31 distinct font sizes with no tokens at all (thirteen of them inside the 0.7–0.95rem
band, where nobody can see the difference) and 28 raw spacing values sitting alongside a
five-step scale too coarse to be usable. If you find yourself typing `font-size: 0.82rem`, the
scale is missing a step — add the step, don't add the value.

**Three breakpoints: 640 / 1024 / 1400.** They are a convention, not tokens, because a custom
property is illegal inside a `@media` condition and `@custom-media` has no native support — the
list lives in a comment in `app.css` and is enforced by review. Two documented exceptions:
Home collapses at **1080px** (the picker-plus-console grid is genuinely tight below that), and
`NavMenu.razor.css` still carries Bootstrap's `767.98/991.98/1279.98` boundaries, where the
fractional part is load-bearing against paired `min-width` rules. Everything else was 13
arbitrary values, and eight stylesheets had no responsive handling at all.

**Shared text and layout primitives, same rule as `.po-btn`.** `.po-page` (+`--narrow`/`--wide`),
`.po-header`, `.po-title`, `.po-subtitle`, `.po-section-title`, `.po-section`, `.po-hint`,
`.po-status`, `.po-error`, `.po-empty`, `.po-chip`, `.po-glass`, `.po-lift`, `.po-glow`. These
replaced ~55 per-surface classes doing eight jobs (11 different `__title`, 9 `__error`,
8 `__status`, 7 `__header`…) — the identical drift that produced twelve competing button
classes. A surface that needs a tweak adds a **layout-only** class alongside the primitive.
Note `home__title`, `archive__title`, `arena__title` and `leaderboard__title` are kept purely as
E2E-UI selector hooks and carry no styling; that suite is not in CI, so removing one fails
silently.

**Wide tables become cards below 640px.** `.po-table--cards` turns each `<td>` into a labelled
row using `data-label` on the cell. The table stays a real `<table>` with real `<th scope>`, so
the accessibility tree is unchanged and `::before` content is not announced twice; only the
visual presentation changes. Cells opt out with `.po-cell--bare`. A table without `data-label`
degrades to the old horizontal scroll rather than breaking.

**There is no component library — `.po-btn` is the only button.** Radzen was removed wholesale
in an earlier pass, re-added on 2026-08-22 for `RadzenDataGrid` (Archive) and `RadzenChart` (model
profile), then **removed again on 2026-08-23**: `Radzen.Blazor` cost 1.43 MB gzipped — 11.9% of
the app's entire download — for two components. The Archive is a `.po-table` again and the profile
chart is inline SVG; both files carry a comment saying so. Do not reintroduce it without re-taking
that payload decision. Buttons and tables are `.po-btn` and `.po-table` in
[app.css](src/PoLocalCompare.Client/wwwroot/css/app.css), styled from design tokens. Twelve
per-surface button classes (`wizard__btn`, `h2h__btn`, `lab__btn`, `source-compare__btn`
…) had each reimplemented the same thing locally and drifted apart; they were folded into `.po-btn`
plus modifiers (`--sm --lg --block --primary --success --secondary --ghost --warn`; `--danger`
went with the model-health panel's Cancel button on 2026-08-23, its only caller). A
surface that needs a tweak adds a **layout-only** class alongside `.po-btn` — `arena__action-btn`,
`archive__btn`, `leaderboard__sort-btn` and `lab-card__icon-btn` are the pattern. New *visual*
variants go in `app.css` as a modifier, never in a `.razor.css`. The two exceptions are deliberate:
`login__ms-btn` and `navmenu__ms-btn` restate a fixed white field because the Microsoft mark is
trademarked artwork with a mandated presentation. Note also the app has no reflective component
instantiation except the Router's `NotFoundPage`, which is the only remaining reason
`PublishTrimmed` is off.

**Every surface owns exactly one BEM block, named after its file.** `NavMenu` → `navmenu__`,
`Login` → `login__`, `Home` → `home__`, `Tournament` → `tourney__`. This is enforced by nothing,
and it has broken twice: `Leaderboard.razor.css` carried both `lb__` and `leaderboard__`, and the
old `LabModelCard` shared `lab__` with its parent panel — which is exactly the scope-id trap below
waiting to happen. Do not introduce a second block into a stylesheet.

**Classes in markup with no rule anywhere are a recurring defect.** The nav bar carried
`nav-item`, `btn-sm` and `btn-outline-warning` long after Bootstrap was gone, and
`arena__source-btn`, `arena__generating-notice`, `auth-spinner`, `h2h__sparkline-col` and
`scorecard__findings-col` all styled nothing. Nothing warns. To check the whole app, diff the
classes used in `.razor` markup against the selectors defined in any `.css`.

**Scoped CSS is per-`.razor`-file, and nothing warns when it isn't.** The since-deleted
`ModelHealthPanel.razor.css` spent a long time styling `LabModelCard`'s markup, which silently
matched nothing because Blazor stamps each stylesheet with its own component's scope id. If you move markup into a child
component, move its rules into that component's own `.razor.css` (or use `::deep` — which is why
`navmenu__link` rules need it, since `NavLink` renders the anchor outside the component's scope).
A class that is built by interpolation — `tourney__status--@_tournament.Status`,
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

**Tournaments run on the server, except the browser matches.** `/tournament` draws a seeded
single-elimination bracket over 2 (a plain 1v1) or 8 models and `TournamentRunner` plays it to
the final on the background queue. Seeding is standard tournament seeding (`1,8,4,5,2,7,3,6`),
not a shuffle: a random draw routinely knocks the two best models out against each other in
round one. Two invariants: a bracket that cannot finish is **Abandoned, never Complete** (naming
the last model standing as champion would invent an outcome), and a drawn match advances the
**better seed**, which is why `BracketSlot` carries a seed number at all. The runner holds no
state — every step re-reads the tournament — so a restart resumes rather than losing the run.

**Browser models may enter a bracket, and that is why `Tournament.razor` holds a hub
connection.** They were excluded until 2026-08-23 because a bracket outlives the tab and WebGPU
inference does not. They are allowed now, and the page pays for it: on every poll it points a
`SignalRDuelClient` at the *running match's* duel group and answers `StartLocalInference` with
`LocalInferenceDriver`, exactly as the Arena does for one duel. The connection follows the match
rather than the tournament, because the server addresses that signal to `duel:{duelId}` and a
bracket is seven different duels; the server re-sends it every 5 s, so joining late still works.
The consequence is real and stated on the page — close the tab during a browser match and it
stalls until the duel's 15-minute watchdog fails it, handing the walkover to its opponent. A
bracket of remote/Ollama models still finishes with nothing open. **The 4-model bracket was
dropped** in the same pass; `BracketPlanner.SupportedSizes` is `[2, 8]` and the maths is
size-generic, so re-adding it is a one-line change.

**Challenge budgets are adjudicated before the judge.** A duel can carry a `ChallengeKind` +
threshold; `ChallengeAdjudicator` runs ahead of `AutoJudge` in `DuelExecutionService`, because a
budget is arithmetic rather than an opinion and must keep working with `AiJudge:Enabled=false`.
One side inside the budget wins outright; both inside falls through to the ordinary judge; neither
inside records a tie. Two measurement rules are load-bearing: a **failed run never meets a budget**
(a crash has a short stored duration, so counting it would make failing fast the winning speed
strategy), and an **unpriced model counts as zero spend** (otherwise every local model is
disqualified from every cost challenge). There is **no challenge leaderboard** — `/challenge`,
`ChallengesEndpoints`, `ChallengeRecord` and the `ChallengeRecords` table were deleted on
2026-08-23. Adjudication was always the part that changes outcomes; the board was a second
ranking of the same duels. The budget picker on Home stays, and a forfeit still moves ELO with
`VerdictSource.Constraint`.

**`autoJudgeDelaySeconds` is a per-duel override, and a tournament is its only caller.**
`TournamentRunner` passes 0 so a bracket never stalls between rounds waiting for a human who is
not there. The override is clamped 0–3600 and cannot switch the judge *on*: `AiJudge:Enabled=false`
still restores human-only verdicts. (`/demo` used to be the other caller — ten client-orchestrated
remote-vs-remote duels that persisted and moved ELO. It was deleted on 2026-08-23: it was a second
implementation of the Arena's streaming UI whose only distinguishing feature was that it died with
the tab, and it wrote real duels into the leaderboard while pretending to be a demo.)

**Motion is compositor-only, and that is a correctness constraint, not a style rule.** Browser
models run WebLLM inference over **WebGPU in this same tab**, and two things depend on that GPU
being free: the tok/s the `TokenRace` reports, and — since challenge mode — whether a model comes
in under a `MaxSeconds` budget. A budget miss forfeits the duel and moves ELO, so a render loop
competing for the GPU would not merely drop frames, it would **record wrong verdicts**. So:
continuous motion is CSS transform/opacity only (`body::before` aurora drift, `.po-lift`,
`.po-glow` in [app.css](src/PoLocalCompare.Client/wwwroot/css/app.css)); `backdrop-filter` is fine
(compositor, not the 3D pipeline); and the only canvas work in the app —
[fx.js](src/PoLocalCompare.Client/wwwroot/js/fx.js) — is one-shot and fires only after inference
has finished (verdict landed, champion crowned). Audio is exempt: it runs on the audio thread and
never touches the GPU, which is why `PlayTokenBlipAsync` is safe to call mid-duel. **Do not add
Three.js, PixiJS, Rapier or a WebGL/WebGPU render loop** without re-deciding this trade-off —
it was considered and declined for exactly this reason.

**Audio is synthesised, never a file.** [audio.js](src/PoLocalCompare.Client/wwwroot/js/audio.js)
builds every cue from oscillators and noise buffers at play time. The previous version fetched
`/audio/snare-roll.wav` and `/audio/success.wav`, both of which were **44-byte stubs** — a RIFF
header with a zero-length data chunk — so every "sound" the app played was silence, invisibly,
for as long as those cues existed. Synthesis removes the class of failure: there is no asset to be
present-but-empty. Note `audio.js` and `fx.js` are `import()`ed with their own `?v=` cache-buster,
the same trap as the `<script src>` tags — bump it when you edit them or the browser serves the
old module.

**Vertical slices.** Server code lives in `src/PoLocalCompare.Api/Features/<Feature>/` — endpoint,
handlers, entities, and repository flat in one folder. `Common/` is only for genuinely cross-slice
code — `Common/Domain/` was dissolved in the 2026-08-13 prune because all four of its calculators
had exactly one consuming slice (`GreenStatsCalculator`, `HtmlOutputNormalizer` and
`HtmlOutputQualityScorer` now live in `Features/Scoring`, `WinRateCalculator` in
`Features/Leaderboard`). There is no Domain/Application/Infrastructure split; it was collapsed in 2026-07-06 (PRD §9).

**Streaming re-renders are coalesced, and the frames opt out.** Token-batch updates arrive far
faster than a frame. `Arena` funnels its per-batch handlers through
`RenderCoalescer.Request()` (~16 ms trailing edge) instead of calling `StateHasChanged` directly,
and `SandboxedViewport` implements `ShouldRender` gated on an ordinal compare of its raw HTML —
without that, every render re-emitted `srcdoc` and the browser tore down and reloaded the preview
mid-generation. Terminal events (`DuelComplete`, verdicts) still paint immediately; don't route
those through the coalescer.

**The verdict write order is load-bearing.** `RecordVerdictHandler` writes the *duel* first,
then the model aggregates, then the ELO history — and that order is a bug fix, not an accident.
Both the decisive path and `RecordTieAsync` used to update the two model rows first. An
optimistic-concurrency 412 on either model write then left one already incremented, and the
retry in `HandleWithRetryAsync` re-ran the whole method and incremented it a second time. The
duel write is the idempotency guard: once it lands, a second pass hits the "verdict already
recorded" check and stops. What hid the bug for so long is that `EloHistoryRepository.SaveAsync`
swallows a 409 as an idempotent append, so history stayed *correct* while `DuelCount` and
`WinCount` silently doubled — three duels reporting `duelCount=6, winCount=4, eloHistoryRows=3`.
`VerdictWriteOrderTests` pins it by asserting the counters against the history they derive from.
The residual risk is deliberately the mirror image: a model write failing after the duel is
written means that rating does not move, which is visible and rebuildable from history, rather
than inventing rating that was never earned.

**Integration tests share one Azurite, so global assertions are order-dependent.** Every class in
the `Integration` collection builds its own `IntegrationHost` against the *same* container. A test
that asserts on a global projection — `board[0]`, `Assert.Empty(board)`, a leaderboard position —
passes or fails on execution order, because sibling tests legitimately contribute rows. Scope
assertions to the models the test created (`board.Single(r => r.ModelId == a)`) and assert
*relative* order rather than absolute position.

**Observability is Serilog and nothing else.** OpenTelemetry (tracing + metrics, the AspNetCore
and HttpClient instrumentations, the OTLP and Azure Monitor exporters) and the
`Serilog.Sinks.ApplicationInsights` sink were all removed on 2026-08-23 — six packages and ~100
lines of `Program.cs` shipping to one App Insights resource by two independent paths, for a
single-instance Free-tier App Service. `RateLimitedSampler` and `InferenceTelemetry` went with
them, so `Common/Telemetry/` no longer exists. What is left is Serilog to console (App Service's
log stream picks that up) plus daily rolling files in Development only. If you need distributed
traces back, re-add the OTel packages — don't half-restore one exporter. OpenAPI/Scalar is
untouched and still mounts at `/scalar` in Development.

**The `/api/dev/*` endpoints require a session.** `POST /api/dev/reset` wipes Duels, DuelResults
and EloHistory and resets every model to 1200. It and `/api/dev/remap-model-ids` are gated on
`IsDevelopment()` *and* `RequireAuthorization()`; they were `AllowAnonymous` until 2026-08-23,
which put an unauthenticated table wipe one `ASPNETCORE_ENVIRONMENT` slip from live data. In
Development the fake-auth handler satisfies the policy from a header, so this costs nothing
locally — don't "simplify" it back.

**Model health lives on `/diag`, in vanilla JS, and that is forced.** The Home page carried a
Blazor `ModelHealthPanel` (plus `LabModelCard`) until 2026-08-23. It is now a section of
[Diag.cshtml](src/PoLocalCompare.Api/Pages/Diag.cshtml) driven by
[diag-models.js](src/PoLocalCompare.Client/wwwroot/js/diag-models.js): one **Test all models**
button and a row per model. It could not be moved as a component — `/diag` is a server-rendered
Razor Page precisely so it works when the WASM client is the broken thing, which is what
`index.html`'s boot-timeout fallback links there for. What made the move cheap is that the
probing half already lived in framework-free `diag-interop.js`; `runModelDiag` still calls back
through something shaped like a .NET object reference because `diag-models.js` hands it a
duck-typed stand-in rather than forking it. The three model types are tested three different
ways, and that is not incidental: **remote** goes through `GET /api/models/availability`, which
already sends a real 16-token completion to each Foundry deployment; **Ollama** posts the prompt
to `/api/ollama/benchmark`; **browser** runs `runModelDiag` in the tab, strictly one at a time,
because they share one GPU and two at once produces "Device was lost" instead of a result. Note
`/diag` is anonymous but `/api/models` is not — the table says "not signed in" rather than
rendering empty.

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
- Restart the app and verify it starts cleanly after a code change (see Working rules).
- Never store local config with `dotnet user-secrets` — `appsettings.Development.json`, an
  environment variable, or Azure Key Vault.
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
