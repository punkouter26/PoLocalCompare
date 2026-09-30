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
  wired through `KeyVault:Uri`. The legacy `UserSecretsId` was removed from the Api csproj on
  2026-09-26 — don't add it back. Secrets never go in code, logs or committed files.

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

```powershell
pwsh SCRIPTS/check-clean.ps1    # repo-hygiene gate: budgets, dead/unused CSS, unreferenced types, cache-busters
```

`check-clean.ps1` is not a linter — it is the things that had already gone wrong silently and that
nothing else watches: a tier over its test budget; a class applied in markup that no stylesheet
defines (the element just renders unstyled); a class a stylesheet defines that nothing applies; a
C# type named nowhere but its own declaration; and a stylesheet or script loaded without a `?v=`
cache-buster. It reads source only, so it is deterministic and cheap, and the `hygiene` job in
[deploy.yml](.github/workflows/deploy.yml) runs it beside the suites.

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
onto whichever tier is easiest to write. **Unit and E2EAPI sit exactly at the cap**
(100/38/25/23 as of 2026-09-26) and `SCRIPTS/check-clean.ps1` fails when a tier exceeds its
budget, so the contract is enforced rather than remembered. A new Unit test has to displace a
weaker one — that is how `ArenaPhaseResolverTests` got in on 2026-09-26 (six trivial id/label
cases made way). Say so rather than quietly raising the cap.

**E2E-UI needs the machine to itself.** The suite drives a real browser against the one running dev
instance, and `SignedInPageAsync` waits for `NetworkIdle` with a 45-second budget. Anything else
loading the same server at the same time — a second Playwright script, a browser tab left open on
`/arena` with a live SignalR connection — pushes cases past that budget and they fail as assertion
timeouts, which reads exactly like a real regression. Observed on 2026-09-22: four cases failed in
a full run and every one of them passed in isolation, three of them in 8 seconds against 48. Re-run
a failure on its own before believing it.

Two async traps in the server-side suites, both of which were causing real failures until
2026-09-10:

- **Duel execution is asynchronous.** `POST /api/duels` returns 202 once enqueued; the two result
  rows land later. A verdict posted before they exist is refused with 409 ("This duel is still
  running") — correctly, because ELO must never move on missing evidence. Tests must wait for the
  rows (`DuelTestFlow.WaitForBothResultsAsync` in Integration, `WaitForBothResultsAsync` in
  `DuelContractTests`) instead of racing them. That race was costing 7–9 of the 50 integration
  cases per run.
- **`BackgroundTaskService` is a SINGLE consumer.** `AutoJudge` runs inline inside it only
  when the grace window is 0 (tournaments); with a window it is detached since 2026-09-26, so a
  standalone duel no longer holds the worker through `AiJudge:DelaySeconds` plus the judge call.
  Anything else that makes a queue item wait still stalls every later test — a browser-model
  duel waiting for a client result that a headless test never posts. That is why the E2EAPI fixture keeps a short watchdog and why its duel suite
  runs in its own collection with the judge off — tournaments cannot run without the judge and
  duels cannot run with it, so they get one host each.

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
afterwards. `Constraint` means **no model's output was read at all**: `AutoJudge` stamps it for a
one-sided failure (the survivor *is* the evidence) and `DuelRecoverySweeper` for a duel abandoned
without results. It began as the challenge-budget forfeit and outlived that feature — challenge
mode was removed on 2026-09-10, and the value stays because those two paths still produce it and
stored rows still carry it.

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

**Every verdict is two judge calls, and they must agree.** `FoundryDuelJudge` asks once with
the left page in slot A and once with it in slot B, in parallel; a pick that flips with the
order is recorded as a **Tie** ("too close to call"), and one call failing defers to the other.
The coin flip it replaced only spread position bias evenly — it never removed it from a verdict.
The schema also puts `reason` **before** `winner`, so the verdict is written after the checklist
rather than rationalised after it. With screenshots on, both sides render in parallel and a page
that looks animated is shot as **two frames 800 ms apart**, because one frame cannot show a
cube that is supposed to rotate. `POST /api/dev/judge-calibration?take=N` replays the current
judge over human-decided duels and reports the agreement rate — run it before and after any
judge change; it never writes a verdict.

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
Gemini models seed only when `Gemini:ApiKey` is set: a `gemini-*` `ApiEndpointRef` is a Remote
model served by Google's OpenAI-compatible endpoint (`FoundryChatRequest.IsGemini` picks the URL
and the Bearer header), not a separate `ModelType`. `DefaultPriceBook` matches the **longest**
key — it used to take the first prefix hit, priced GPT-5.4 Mini as GPT-5.4 and overwrote the
correct seed rates with that on every startup.

**Home renders the catalog before the health probe lands.** `GET /api/models/availability`
sends a real 16-token completion to every Foundry deployment before it answers — measured at
4,850ms against 2,630ms for the model list — and `LoadAsync` used to await all three calls
before clearing `_loading`, so the whole picker sat behind a skeleton for those extra seconds
to decide a header badge and which cards to hold back. The health pair is folded in afterwards
through `ApplyHealthWhenReadyAsync`, unguarded and not awaited: if it never returns the page
stays as it is with every model selectable, which is the right degradation.

**Models can also be added at runtime from `/catalog`**, which is the way round the seed-only-when-empty
rule without wiping Azurite. `GET /api/models/discover` lists browser builds parsed out of the
vendored `web-llm.js` (`WebLlmBundleCatalog` — the same parse as `plan-webllm-artifacts.py`),
ranked by Hugging Face Hub download counts, plus pulled Ollama tags in Development.
`POST /api/models/discovered` **re-checks** the id against the bundle or the daemon before
registering, and it is the only way to add a model: the unchecked `POST /api/models` (any
`WebLlmModelId`, typos included) and `DELETE /api/models/{id}` were removed on 2026-09-26 — tests
seed the registry through the repository (`TestModels` in each server-side test project). A runtime-added model is not in `ModelSeeder`, so
`download-models.py` will not vendor its weights — it streams from the CDN unless you also add it to
the seeder.

**The leaderboard's `±N` is a Bradley–Terry interval, not a property of ELO.** `BradleyTerry`
fits every judged duel at once (MAP with a 400-point Gaussian prior) and reports each model's 95%
interval *relative to the mean of the models that have played* — BT only identifies differences,
so an absolute interval would carry the prior's scale uncertainty forever and never narrow. It is
read-side only; ranking is still `CurrentElo`. Each duel is written into both models' history
partitions, so `GamesFrom` de-duplicates on duel id — counting both halves would halve every band.

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

**Home's console is `max-height`, and the phone picker is a bounded window.** The console
was a FIXED `height: calc(100dvh - 6.5rem)` whatever it contained — 796px on a 1440x900
desktop, of which ~278px was empty glass between the prompt-library button and the Compare
footer. A maximum keeps everything the fixed height was there for (the foot stays pinned, only
the middle scrolls, it can never grow past the fold) and lets it shrink to its content. Below
1080px (one column) the picker is capped at `52dvh` — it was only below 640px until 2026-09-26,
so tablets still had the whole catalog above the prompt — and `home__console-foot` becomes a
**`position: fixed` bottom bar**, because on a 390px phone Compare measured at y≈1050, below the
fold, and a sticky foot cannot escape a console whose own top is below the fold. The console
drops `.po-glass`'s `backdrop-filter` there: a filtered ancestor is the containing block for
`position: fixed`, so the bar would pin to the console instead of the viewport. Below 640px
`ModelCard` drops its parameter and pricing rows, so the window shows ~4 models rather than
1.5; the starter-duel control is a single button beside
the picker heading, because at the bottom of an unbounded list it measured at
y=3735 inside a 337px box — the one control aimed at a first-time user was the least reachable
thing in the app.

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

**The Arena's status band is one region driven by one enum.** `ArenaPhaseResolver` in
`Shared/Presentation` is a pure function from thirteen inputs to a single `ArenaPhase`, and
`ArenaStatusBand` renders it. That replaced nine independent flags (`_duelStillRunning`,
`_verdictRecorded`, `_optimistic`, `_verdictValue`, `_verdictSource`, `_autoJudgeDeciding`,
`_autoJudgeRemaining`, `WaitingForOtherModel`, `BothSidesFailed()`) read across ~12 mutually
exclusive arms in two separate `@if` chains, each carrying its own `role="status"` — ten live
regions on one page, several able to fire in the same frame. Consistency came from arm
*order*, which is a property you cannot test and cannot see; the same trap already documented
on Home's `CompareHint`. It is in `Shared` rather than beside the page for the reason given
below: `PoLocalCompare.Unit` cannot reach anything under `src/PoLocalCompare.Client/`.
`ArenaPhaseResolverTests` pins the precedences that were load-bearing.

**The Arena is the whole duel — streaming and judging.** `/processing` no longer exists; `POST
/api/duels` navigates straight to `/arena/{id}`, which connects to `DuelHub`, shows live
tokens and tok/s inside each panel's `TelemetryHud` (the separate `TokenRace` panel was folded
in on 2026-09-26 — it showed the same numbers the HUD shows afterwards) with the race ticker in
the status band, then swaps to the verdict UI on `DuelComplete`. The winner is marked once:
the ELO delta rides in the viewport's WINNER/LOSER badge (`EloShiftBadge` is gone). Critically, **Arena drives browser-model inference**: it handles
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

**`.po-page` is the only page shell, and the gutter is declared exactly once.** Every route
puts `po-page` (or `po-page--wide`) on its container; `.home`, `.arena`, `.archive` and
`.leaderboard` are kept as E2E-UI and view-transition hooks but carry no geometry. Until
2026-09-22 those four rolled their own `max-width` and padding *on top of*
`article.app-content`'s responsive gutter, and the two composed rather than replacing each
other — measured on one 390px phone, the content column was 358px on `/`, 334px on
`/leaderboard` and 318px on `/archive` (18% of the screen in nested padding), and on a 1440px
desktop the content edge jumped between 1200, 1360 and 1400 as you used the nav. So
**`.po-page` sets `padding-inline: 0`** and `article.app-content` owns the horizontal gutter
(`clamp(1rem, 2vw, 1.5rem)`, composed with the safe-area inset via `max()`). Don't give a page
its own inline padding; if a route needs a different width, use the `--wide` modifier or add a
modifier here.

**`--nav-height` exists now (`3.5rem`).** `NavMenu.razor.css` reads it for its own
`min-height`, and it is what `calc(100dvh - var(--nav-height) …)` refers to — that instruction
had been in `app.css` for a while pointing at a token that was never declared, so the one calc
the codebase tells you to write would have been dropped as invalid.

**Status tints are the `--*-surface` / `--*-border` tokens, never an `rgba()` literal.** The
pairing is `--x-surface` for the fill, `--x-border` for the edge and the plain `--accent-x` for
the text (`--cyan-surface`/`--cyan-border` were added on 2026-09-22 for the nav ticker and
Home's slot tags). About twenty surfaces used to hand-roll the same effect as a **translucent**
literal, and that broke the guarantee twice: a translucent tint composites against whatever is
behind it, so the nav's ticker pills measured 4.26:1 and 4.45:1 against `--surface-base` while
the identical treatment passed on a card; and every literal was the **dark-theme** accent value
frozen into the stylesheet, so a light-theme viewer got a dark palette's hue behind light
palette text. The tokens are opaque, so the contrast of `--accent-x` on `--x-surface` is the
same wherever the chip lands (light 4.53–5.03, dark 4.78–9.18).

**`.po-btn--chip` is the only filter chip.** Home's model-type filters and `PromptPicker`'s
categories were private implementations of one control, and only `PromptPicker` set
`aria-pressed` — elsewhere the active filter was conveyed by colour alone (SC 1.4.1, SC 4.1.2).
Every caller is a real `<button>` with `aria-pressed`, and the active state changes fill *and*
border weight so it survives greyscale. The Archive's All/Won/Tie/Unjudged chips were removed
on 2026-09-30 to keep that page simple — it always lists every duel, and the nav's "awaiting
judgment" pill now links to plain `/archive`.

**Every page title is `<PageHeader>`, and the four title hooks carry no styling.** `home__title`,
`archive__title`, `arena__title` and `leaderboard__title` exist so E2E-UI can address one
page's h1; they are passed as `<PageHeader TitleHook="…">` and have no rule anywhere. All four
once drifted into full rulesets over `.po-title` (43px, 43px and 31px h1s on one desktop); the
gap under a title belongs to `.page-header`. ModelProfile is the one exception — its header is
a shareable card (`.po-header` + `profile__card`), not a page title row.

**Fonts load from `index.html`, not from an `@import` in `app.css`.** An `@import` inside a
stylesheet is invisible to the preload scanner — it cannot be discovered until `app.css` has
been fetched *and* parsed — so the font request was serialised behind it. The weights requested
are the ones actually declared (400/600/700/800 Space Grotesk, 400 JetBrains Mono); the old
request carried a 500 no rule uses and omitted the 800 that `.home__slot-vs` does.

**Shared text and layout primitives, same rule as `.po-btn`.** `.po-page` (+`--narrow`/`--wide`),
`.po-header`, `.po-title`, `.po-subtitle`, `.po-section-title`, `.po-section`, `.po-hint`,
`.po-status`, `.po-error`, `.po-empty`, `.po-glass`, `.po-lift`, `.po-glow`. These
replaced ~55 per-surface classes doing eight jobs (11 different `__title`, 9 `__error`,
8 `__status`, 7 `__header`…) — the identical drift that produced twelve competing button
classes. A surface that needs a tweak adds a **layout-only** class alongside the primitive.
Note `home__title`, `archive__title`, `arena__title` and `leaderboard__title` are kept purely as
E2E-UI selector hooks and carry no styling; that suite is not in CI, so removing one fails
silently.

**Wide tables become cards below 640px.** `.po-table--cards` turns each `<td>` into a labelled
row using `data-label` on the cell. The table stays a real `<table>` with real `<th scope>`, so
the accessibility tree is unchanged and `::before` content is not announced twice; only the
visual presentation changes. Cells opt out with `.po-cell--bare`, hide with
`.po-cell--secondary`. Prefer merging narrow cells over pairing them: the leaderboard's W and L
became one `W–L` Record cell on 2026-09-26, which removed the only `.po-cell--half` caller. A table without
`data-label` degrades to the old horizontal scroll rather than breaking.

**There is no component library — `.po-btn` and `.po-table` are the primitives.** Radzen has now
been added and removed twice. The last removal (2026-09-26) took out `Radzen.Blazor` — +3.26 MB,
+25.7% of the gzipped download — for two grids and one chart: the Archive and Catalog are plain
`.po-table--cards` tables and the profile's rating curve is an inline-SVG polyline. Do not bring a
component library back without re-taking that payload decision.

**`/catalog`'s browser list is a bounded window that virtualises.** It is the longest list in the
app (~150 WebLLM builds, each with a live name `<input>`), so it renders through Blazor's own
`<Virtualize>` inside `.catalog__grid-wrap` (`min(34rem, 60dvh)`, the same shape as
`.home__picker` — a picker is a list you scan *within*). The height must be **definite**: with
only a max-height the virtualiser has nothing to measure against and renders every row — measured
once at 24,014px (28 screens) on a phone. The Archive needs no virtualiser: it pages 20 at a time.

**The Archive pages by duel id.** Ids are ULIDs, so id order is creation order: `GET /api/duels`
takes `before=<duelId>` and returns rows with `RowKey lt` that id, newest first. It used to send a
`yyyyMM` month with `PartitionKey le`, which includes the current month, so Load More returned
the same rows forever. The server still accepts `verdict=` filters, but the client no longer
sends any: the page has no verdict chips (removed 2026-09-30). Re-run starts the
duel directly with the full prompt, like the Arena's Retry — it used to pre-fill Home with the
80-character summary through an unescaped query string.

Buttons and tables are `.po-btn` and `.po-table` in
[app.css](src/PoLocalCompare.Client/wwwroot/css/app.css), styled from design tokens. Twelve
per-surface button classes (`wizard__btn`, `h2h__btn`, `lab__btn`, `source-compare__btn`
…) had each reimplemented the same thing locally and drifted apart; they were folded into `.po-btn`
plus modifiers (`--sm --lg --block --primary --success --secondary --ghost --warn`; `--danger`
went with the model-health panel's Cancel button on 2026-08-23, its only caller). A
surface that needs a tweak adds a **layout-only** class alongside `.po-btn` — `leaderboard__row-btn`
and `archive__row-btn` are the pattern. New *visual*
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

**A CSS class used as a JS selector is a rename that fails silently.** `Arena.razor` names
the verdict band for `fx.js`, Home names the Compare button, `ModelProfile` names its card and
`Tournament` names its champion strip. `fx.js` answers a selector that matches nothing by doing
nothing at all, so a renamed class turns a payoff effect off without a console warning and
without a failing test — which is what happened to `".arena__verdict-recorded"` when that block
was folded into `ArenaStatusBand`. Where a selector is used more than once it is a named
constant (`VerdictBandSelector`), so at least the uses cannot drift apart.

**Classes in markup with no rule anywhere are a recurring defect.** The nav bar carried
`nav-item`, `btn-sm` and `btn-outline-warning` long after Bootstrap was gone, and
`arena__source-btn`, `arena__generating-notice`, `auth-spinner`, `h2h__sparkline-col` and
`scorecard__findings-col` all styled nothing. `check-clean.ps1` now checks both directions — markup
classes no stylesheet defines, and stylesheet classes nothing applies (eight of those had piled up
by 2026-09-26, `.po-chip` among them).

**Scoped CSS is per-`.razor`-file, and nothing warns when it isn't.** The since-deleted
`ModelHealthPanel.razor.css` spent a long time styling `LabModelCard`'s markup, which silently
matched nothing because Blazor stamps each stylesheet with its own component's scope id.

It happened again, and on the Arena. `ArenaViewportPanel`, `ArenaFailureCard`,
`ArenaVoteButtons` and `EloShiftBadge` (since deleted) were extracted out of `Arena.razor` but left their
`arena__*` rules behind in `Arena.razor.css`, so **none of those rules had ever applied** —
verified in a browser on 2026-09-22, where the panel computed `display: block` against the
declared `flex` and `.arena__hud` computed `container-type: normal` against the declared
`inline-size`, which means `TelemetryHud`'s `@container` queries had been matching nothing for
the whole life of the component. Each of the four now has its own `.razor.css` **and its own
block named after its file** (`arena-panel__`, `arena-failure__`, `arena-vote__`,
plus `arena-band__` for the status band) — sharing one `arena__` prefix
across five files is exactly what let the rules end up in the wrong stylesheet.
**`check-clean.ps1` cannot catch this**: it asks whether a rule exists *somewhere*, not whether
it can reach the markup. Moving markup into a child component means moving its rules too. If you move markup into a child
component, move its rules into that component's own `.razor.css` (or use `::deep` — which is why
`navmenu__link` rules need it, since `NavLink` renders the anchor outside the component's scope).
A class that is built by interpolation — `tourney__status--@_tournament.Status`,
`leaderboard__type-badge--@ModelTypeGroup.CssModifier(t)` — will also read as dead to any text
scan, so check for those before deleting a rule.

**Client code that isn't a component doesn't live in `Components/`.**
`src/PoLocalCompare.Shared/Presentation/` holds the view-models, enums and static helpers that
`.razor` files lean on (`ArenaPhase`, `ModelTypeGroup`, `SideMetrics`, `FailureReasonText`,
`RenderCoalescer`). It used to be `Client/Presentation/`, which put it in the
one assembly no tier but E2E-UI can reach — the same trap as the note above, so it moved wholesale.
`src/PoLocalCompare.Client/Services/` keeps what genuinely needs the browser: JS interop, the
SignalR client, and `LocalInferenceDriver` (which runs a WebGPU model in the tab and POSTs the
result back — extracted out of `Arena.razor`, which was the only thing that knew how).

**The Arena's scorecard must never feed ELO.** `OutputAnalysis.CompletenessScore` is presentational
and deliberately separate from the persisted `OutputQualityScore` — tightening a heuristic there must
not retroactively change a stored duel. (The runtime probe that used to inject a reporter `<script>` into the preview is gone —
`SandboxedViewport` renders the model's document untouched.)

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

**The 2026-09-10 prune removed the last of the low-traffic surface.** Challenge mode is gone
entirely — `ChallengeKind`, `ChallengeRules`, `ChallengeAdjudicator`, `Features/Challenges/`,
`Shared/Challenges/`, the `challengeKind`/`challengeThreshold` request fields, the duel columns in
Table Storage, and the Arena's budget rendering. It was cut because **nothing in the UI ever set
one**: a challenge was reachable only by posting a raw `challengeKind` body, so the Arena rendered
a budget line for a field the app could not produce. Also removed in the same pass, each because
it had no caller: `POST /api/models/{id}/download` + `DownloadModelHandler`;
`GET /api/leaderboard/{id}/killlist` (the profile payload already carries `KillList`);
`DuelApiClient`'s `GetKillListAsync`, `GetOllamaAvailableModelsAsync` and
`BenchmarkOllamaModelAsync`; the `CommandPalette` component and its Ctrl/⌘-K handler;
`PlayTensionPulseAsync` (the challenge countdown cue); `api/requests/*.http`; `azure.yaml` (an
`azd` manifest nothing invoked — the workflow uses raw `az` CLI); and `SCRIPTS/test-browser-models.*`
(a second, parallel implementation of the `/diag` browser-model probe). **Two deliberate survivors**:
`VerdictSource.Constraint`, and `wwwroot/push-sw.js` — 601 bytes that exist to answer a speculative
Chromium fetch, so deleting it buys a 404 in the network tab, not simplicity.

**`Features:UseRealAi` is real now, and it was a lie before.** Until 2026-09-10 the setting was
read by the `USING MOCK DATA` banner and the `/diag` config dump and nothing else, so a run with
it off still called Foundry while the UI said the responses were simulated. `MockInferenceProxy`
is what makes the banner true: with the flag off it **replaces** the `Remote`/`LocalService`
keyed proxies rather than being appended after them. That distinction matters — MS.DI resolves the
*last* matching keyed descriptor, so an appended mock would silently outrank the keyed mocks that
`IntegrationHost` and `ApiAppFixture` register for the same keys. Browser (WebGPU) models are not
mocked either way: they run in the tab and the server never sees that inference, which is why the
banner names only remote and Ollama.

**`autoJudgeDelaySeconds` is a per-duel override, and a tournament is its only caller.**
`TournamentRunner` passes 0 so a bracket never stalls between rounds waiting for a human who is
not there. The override is clamped 0–3600 and cannot switch the judge *on*: `AiJudge:Enabled=false`
still restores human-only verdicts. (`/demo` used to be the other caller — ten client-orchestrated
remote-vs-remote duels that persisted and moved ELO. It was deleted on 2026-08-23: it was a second
implementation of the Arena's streaming UI whose only distinguishing feature was that it died with
the tab, and it wrote real duels into the leaderboard while pretending to be a demo.)

**Motion is compositor-only, and that is a correctness constraint, not a style rule.** Browser
models run WebLLM inference over **WebGPU in this same tab**, and the tok/s the Arena reports
is measured while that is happening. A render loop competing for the GPU would not merely drop
frames, it would **make the number the app exists to report wrong** and slow a browser model's own
generation while it is being timed. So continuous motion is CSS transform/opacity only
(`body::before` aurora drift, `.po-lift`, `.po-glow` in
[app.css](src/PoLocalCompare.Client/wwwroot/css/app.css)); `backdrop-filter` is fine (compositor,
not the 3D pipeline); and the only canvas work in the app — the confetti burst in
[fx.js](src/PoLocalCompare.Client/wwwroot/js/fx.js) — is one-shot, fires only after inference has
finished (verdict landed, champion crowned) and stands down while the GPU lease
(`window.poGpuLease`) is held. **Do not add Three.js, PixiJS, Rapier or a WebGL/WebGPU render loop**
without re-deciding this trade-off — the WebGL backdrop that was the one exception went in the
2026-09-26 prune, along with infinite `box-shadow`/`background-position` animations that were not
compositor-only either.

**Audio is three synthesised cues, never a file.** [audio.js](src/PoLocalCompare.Client/wwwroot/js/audio.js)
builds a click tick, a verdict arpeggio and a tie from oscillators at play time. An earlier version
fetched two WAVs that were **44-byte stubs**, so every "sound" was silence, invisibly; synthesis
removes the class of failure. It runs on the audio thread and never touches the GPU. Note `audio.js`
and `fx.js` are `import()`ed with their own `?v=` cache-buster, the same trap as the `<script src>`
tags — bump it when you edit them or the browser serves the old module.

**The 2026-09-26 prune cut the decoration.** Gone, each as cost with no user-facing job: the
sound design beyond the three cues (drone, per-model duet melodies, heartbeat, crowd roar, gavel,
fanfares), fx.js's photo-finish strip, shard/mote/pyrotechnic effects and WebGL backdrop, the
Arena's picture-in-picture race window, haptics, the typewriter rationale, ELO "upset" odds
(`EloUpset` duplicated the server's K-factor on the client), the leaderboard time machine, the
profile's holographic trading card and PNG export, and the bracket comet/camera. Server side:
`OrphanModelIdRemapper` + `/api/dev/remap-model-ids` (a one-shot local-data repair),
`ProgramBootstrapVerifier`, an unused `SecretClient` registration, `GET /api/ollama/available-models`,
and the hand-rolled SSE framing (`SseChatStreamReader` now sits on `System.Net.ServerSentEvents.SseParser`).
Don't reintroduce any of it without a user-facing reason.

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
*relative* order rather than absolute position. Tournaments are the sharpest case: only one may be
in flight, and with the judge off a bracket never finishes on its own, so `TournamentTests` abandons
whatever an earlier test left running before each test.

**Observability is Serilog and nothing else.** OpenTelemetry (tracing + metrics, the AspNetCore
and HttpClient instrumentations, the OTLP and Azure Monitor exporters) was removed on 2026-08-23 —
two independent paths to one App Insights resource on a single-instance Free-tier App Service.
`RateLimitedSampler` and `InferenceTelemetry` went with it. What is left is Serilog to console
(App Service's log stream), daily rolling files in Development only, and ONE
`Serilog.Sinks.ApplicationInsights` sink — re-added with the shared Po platform conventions so the
cross-app `UserSignedIn` record (`Auth/SignInTelemetry.cs`) reaches the shared App Insights; it is
only added when a connection string is configured. Keep it the single exporter. If you need distributed
traces back, re-add the OTel packages — don't half-restore one exporter. OpenAPI/Scalar is
untouched and still mounts at `/scalar` in Development.

**The `/api/dev/*` endpoints require a session.** They live in `Features/Diagnostics/DevEndpoints.cs`.
`POST /api/dev/reset` wipes Duels, DuelResults and EloHistory and resets every model to 1200;
`POST /api/dev/judge-calibration` is the judge agreement check. Both are gated on
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
pipelines; adding a per-attempt timeout will abort SSE streams. Every table is created once at
startup, in every environment, by `StorageTables.EnsureAllAsync` (fail-fast after five 2 s retries,
which covers Azurite still starting); repositories do not create their own tables.

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
- When `Features:UseRealAi` is off, the `USING MOCK DATA` indicator must render (`NavMenu.razor` —
  a compact MOCK chip beside the brand since 2026-09-26, the full sentence in its title and a
  visually-hidden span).
- UI targets **WCAG 2.2 Level AA**: keyboard-operable custom controls, `:focus-visible` ring, 24×24
  minimum target size, `role="status"` for live updates, `aria-hidden` on decorative glyphs. Colour
  contrast is not automatically checked — verify new palette tokens by hand, **in both themes and
  against every surface the token lands on**. Four failures were found that way on 2026-09-22 and
  all four came from a literal rather than a token: `#fff` on `--accent-blue` (2.54:1 in dark — the
  app's *primary* button), `#fff` on `--accent-red` twice (3.76:1 in dark), and white on the
  `--remote` / `--localservice` type chips' gradients (3.68:1 and 2.43:1, judged at the light end,
  under a comment asserting they were "well past 4.5:1"). **`--on-accent` is what goes on an accent
  fill** — it is white in light and black in dark, and it already existed; the failing rules simply
  were not reading it. A gradient is judged at its lightest stop.
- **A focus ring is never replaced by a translucent shadow.** `.home__textarea` did
  `:focus { outline: none }` — `:focus`, not `:focus-visible`, so keyboard users lost it too — and
  substituted `0 0 0 3px rgba(18,184,207,0.18)`, which cannot reach the 3:1 of SC 1.4.11, on the
  primary input of the page the app exists for. A scoped `.class:focus` also outranks the global
  `:focus-visible`, so nothing put it back.
- Styling is scoped `.razor.css` + design tokens; there are **no** inline `style=` attributes or
  `<style>` blocks left. A genuinely dynamic value (a progress width, an animation stagger) is passed
  as a CSS custom property — `style="--fill: 42%"` — and consumed by a rule in the stylesheet, so the
  styling itself stays in CSS. Colour tokens are declared for light, for `prefers-color-scheme: dark`,
  and again under `:root[data-theme=...]`; the `[data-theme]` blocks must stay last or the header's
  theme toggle cannot override the OS preference.
- **`box-sizing: border-box` is global** (`app.css`, a `*, *::before, *::after` rule). It was absent
  until 2026-09-10, so every `width: 100%` + padding rule rendered wider than its container and the
  excess was silently **clipped** by `article.app-content { overflow-x: clip }` — `.po-page` resolved
  to 407px inside a 375px parent on a phone, which cut "ELO" off the model-profile header and defeated
  `.po-table--cards`. Don't remove it, and don't reintroduce a `width: 100%` box that relies on being
  able to overflow.
- **Nothing inside `main` may set `min-height: 100dvh`.** `main` already starts below the 56px sticky
  nav, so a `100dvh` minimum there made *every* route 72px taller than the viewport (measured:
  `scrollHeight` 972 against `innerHeight` 900 on all six routes). The viewport guarantee belongs to
  `.page` alone; a descendant that needs a floor must use `calc(100dvh - var(--nav-height))`.
- **`index.html` cache-busts the stylesheets with `?v=N`**, matching what the `<script src>` tags
  already did. Without it the browser serves the previous build's CSS from its HTTP cache (the
  `service-worker.js` WebLLM registers is a pass-through no-op) and a change simply does not appear — this cost real time when verifying the box-sizing fix, which
  was briefly "confirmed" against a stale sheet. **Bump the number on every CSS edit.**

## Known stale documentation

The original "GPT-4.1 Nano auto-judges after the 24-hour deadline" service is gone, and so is the
forfeit auto-award that replaced it. Auto-judging now exists again but works differently from both:
the trigger is a short grace window after the duel finishes (`AiJudge:DelaySeconds`), not a 24-hour
deadline, and the judge is whatever `AiJudge:Deployment` names. See the verdict section above.
