# Per-route contract (Review #7)

For every user-facing route, this document records what it MUST show, what it MUST NOT show, and where the assertion lives. PRD §8 makes "no UI link to `/health`/`/diag`" a contract; PRD §4.5 makes auth deny-by-default; PRD §7 makes `usingAuth`/`signalR` boundary a contract; PRD §9 item 18 made the `/health`/`/diag` rule and the mock-data banner ("USING MOCK DATA" when `Features:UseRealAi=false`) explicit; PRD §9 item 19 added the same to tie/verdict attribution.

Each row is one assertion that, if a future refactor breaks it, *will* fail a test in `tests/PoLocalCompare.E2EAPI` or `tests/PoLocalCompare.E2EUI`. Today these are aspirational — turn each row into a test in the appropriate project.

## Matrix

| Route | Auth | MUST show | MUST NOT show | Tested by (proposed) |
|---|---|---|---|---|
| `/auth/login/fake?returnUrl=/` (Login) | Anon | "Sign in with Microsoft"; in non-Prod, "Continue as Guest" | Cookie; any duel data | `E2EUI.LoginRenders` |
| `/` (Home) | Auth | "Compare two models"; the four filter chips; the two-slot state; the prompt textarea; the Compare CTA | Any link to `/health` or `/diag`; any line reading "USING MOCK DATA" if `Features:UseRealAi=true`; resolved model id rendered as the display name | `E2EUI.Home_*` (existing) + `E2EUI.NoDiagnosticLinksOnHome` |
| `/arena/{id}` | Auth | Either a streaming race with both viewports, OR a verdict recorder with two winner buttons; if both sides failed, the "Both models failed" notice and *no* winner buttons | "Pending" CTA ever enabled after a verdict; "view source" containing the runtime probe `<script>` | `E2EUI.Arena_*` (existing) + `E2EAPI.ArenaNotFound` |
| `/arena/none` | Auth | The "Duel not found" message; a link back to `/` | An enabled vote button; a streaming race | `E2EUI.ArenaNotFound` |
| `/leaderboard` | Auth | The ELO/W-L/T table; the ELO/🧪/💰 sort buttons; the kill-list disclosure on row activation | A row that does not have a name; a "Green Score" column | `E2EUI.Leaderboard_Renders` |
| `/archive` | Auth | The duels table with the verdict column named as the winner; a per-row "Judge"/"Tie"/"🏆 Name" cell; the "Load More" button when the server has more | A count line that conflates filter / loaded / server populations; a column with chevrons for "open row" | `E2EUI.Archive_Renders` |
| `/demo` | Auth (or anon if `Features:AllowAnonymousWrites`) | The demo-warning line stating "These are real duels… judged by the AI judge, and they move ELO." | The Start button while no model is selectable | `E2EUI.Demo_WarnsBeforeStart` |
| `/notfound` (or any unknown route) | Auth | The 404 page | A blank page; the same content as `/` | `E2EUI.NotFound_Renders` |

## Server-side contract

| Endpoint | MUST | MUST NOT |
|---|---|---|
| `GET /health` | Return 200 + JSON `{status, components}`; no auth | Reveal the BFF cookie or session state |
| `GET /api/diag/smoke` | Return 200; mask any `ApiKey`/`ClientSecret` substring (PRD §8) | Leak the Foundry key, Key Vault URI, or any other secret |
| `POST /api/duels` | 202 + Location; queue via `DuelExecutionService`; reject when the same model is on both sides (PRD §1 "two models"); reject empty `PromptText` | Auto-judge enable/disable is *not* controllable via this endpoint (PRD §9 item 9: `AiJudge:Enabled=false` only) |
| `POST /api/duels/{id}/verdict` | Throw `InvalidOperationException` on second verdict (409); throw `ArgumentException` on `Pending`/`Expired` (422); throw `InvalidOperationException` when both sides failed and verdict is `Left`/`Right` (PRD §9 item 18); carry `VerdictSource` (`Human`/`Ai`) and `JudgeRationale`/`JudgeModel` (PRD §9 item 9) | Move ELO without `VerdictSource` set (PRD §9 item 9) |
| `GET /api/leaderboard/{modelId}/killlist` | Return 200 + empty list when the model is unknown | 404 for unknown model (PRD §9 item 19) |
| `GET /api/duels/{id}/report` | Return 200 + text/html with `Content-Disposition: attachment`; return 404 when the duel id is unknown | Run the runtime probe inside the report's iframe (PRD §9 item 13) |

## Hidden from UI (PRD §8)

- `/health` (JSON)
- `/diag` (Razor)
- `/api/diag/smoke`
- `/api/diag/warnings`

Search the rendered HTML for the substrings `/health`, `/diag`, `/api/diag/` on every page; any hit is a contract violation. (None observed during the audit pass on Home.)

## TODO (one-sentence pre-condition for "we can stop auditing")

> Every row above is a green test in `E2EAPI` or `E2EUI` for both viewports (390×844, 1440×900), both themes (light, dark).

The crawl script in `SCRIPTS/review/crawl.cjs` already does this for axe-core; the "must show" half is the missing row. Port the matrix into `tests/PoLocalCompare.E2EUI` and `tests/PoLocalCompare.E2EAPI` (one `[Theory]` per row) and the contract becomes a build gate.