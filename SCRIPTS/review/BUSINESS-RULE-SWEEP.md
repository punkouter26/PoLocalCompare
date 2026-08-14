# Business-rule sweep (Review #10)

For every server-side guard, this lists the rule, the path it lives on, and whether the rule is still pulling its weight. PRD §9 items 9, 13, 18, 19 each *added* a guard; this sweep looks for any that could be removed because the bug they patched no longer exists.

## `RecordVerdictHandler.HandleAsync` (Duels)

| Rule | Path | Status |
|---|---|---|
| Verdict must not be `Pending` | first guard | **Keep** — caller maps to 422 (PRD §3) |
| Verdict must not be `Expired` | second guard | **Keep** — PRD §3 routes the caller to the expiration workflow |
| Duel must exist | third guard | **Keep** — returns null → 404 |
| Duel already has a verdict | fourth guard | **Keep** — `InvalidOperationException` → 409 (PRD §5.5 ETag rule) |
| Duel has expired (deadline) | fifth guard | **Keep** — same 409 |
| `GuardAgainstNoEvidenceAsync` | sixth guard | **Keep** — PRD §9 item 18 (the no-evidence rule that PRD §9 item 9 missed for two weeks) |
| Tie → `RecordTieAsync` | branch | **Keep** — PRD §9 item 19 |
| `K-Factor` from config | constructor | **Keep** — K=32 default; PRD §3 |

### Findings

- **No redundant guards.** Each one was added to fix a specific bug.
- **Two non-obvious behaviors worth a one-line comment near the call site**, in case a future reader thinks them vestigial:
  1. `GuardAgainstNoEvidenceAsync` — only blocks when *both* sides have results and both are failures; a one-sided failure is still a walkover (PRD §9 item 9).
  2. `RecordTieAsync` — a Tie moves no ELO; `EloCalculator.Calculate` is never called on this path. Comment says so, but the handler name doesn't. Consider renaming to make this obvious.

## `AutoJudge.DecideAsync`

| Rule | Status |
|---|---|
| Verdict must still be `Pending` (re-read on entry) | **Keep** — the human-races-the-judge invariant (PRD §9 item 9) |
| Judge prompt constrains reply to `A`/`B`/`Tie` | **Keep** — without it the judge prompt's open-ended reply could parse as anything (PRD §9 item 19) |
| Unparseable reply → leave duel `Pending` | **Keep** — PRD §9 item 9 "the no-evidence rule" |
| Position-bias mitigation (coin flip on slot assignment) | **Keep** — PRD §9 item 9 |

### Findings

- **No findings.** AutoJudge is well-defended.

## `CommenceDuelHandler.HandleAsync`

| Rule | Status |
|---|---|
| `PromptText` non-empty (delegated to endpoint validation) | **Keep** — returns 422 |
| Two distinct model ids | **Keep** — same-model twice is not a duel |
| `AutoJudgeDelaySeconds` clamped 0–3600 (delegated to endpoint) | **Keep** — caller can't park a judge an hour out |

### Findings

- **No findings.**

## `DuelExecutionService` (the inference orchestrator)

PRD §1: "three inference paths converge." The handler is the hub.

| Rule | Status |
|---|---|
| Browser model → `WebLlmService` (client-side); server only sees `OnStartLocalInference` then the result via `POST /api/duels/{id}/local-result` | **Keep** — PRD §1, §9 item 13 |
| Remote / Foundry → typed HttpClient "Foundry" + retry-only resilience | **Keep** — PRD §4.1 (SSE would break with per-attempt timeout) |
| Ollama / `LocalService` → typed HttpClient "Ollama" | **Keep** — dev-only |
| 900s inference cap (duel watchdog) | **Keep** — PRD §8 |
| Failed models never auto-awarded against (the no-evidence rule from PRD §9 item 9) | **Keep** — single-failure walkover is decidable; both-failure is not |

### Findings

- **No findings.**

## `OrphanModelIdRemapper` (Models)

The remapper is the only `Features/Models/` file that has no `MapXxxEndpoints` consumer — it's invoked at startup in Development and by hand via `POST /api/dev/remap-model-ids`. PRD §9 item 19 added it to repair the catalog-re-key fallout.

| Rule | Status |
|---|---|
| Insert-then-delete on ELO history (partition key is model id) | **Keep** — comment explains the order; without it an interrupted run *loses* a row |
| Match by `DisplayName` snapshot (no fuzzy match) | **Keep** — PRD §9 item 19; the duel row carries the snapshot |
| Skip orphans whose name matches nothing | **Keep** — burnt seeds 007/008, deliberate |
| Idempotent — second run is a no-op | **Keep** — comment-asserted |

### Findings

- **No findings.**

## `ModelsEndpoints` (Models)

| Endpoint | Rule | Status |
|---|---|---|
| `POST /api/models` | `DisplayName` required | **Keep** — 422 on empty |
| `POST /api/models` | Conflict on duplicate | **Keep** |
| `DELETE /api/models/{id}` | — | **No callers**, **no tests** → **prune** (see INTENT-AUDIT) |

## `LeaderboardEndpoints`

| Rule | Status |
|---|---|
| `GetKillListHandler` returns empty list (not 404) for unknown model | **Keep** — PRD §9 item 19 |
| HybridCache tag invalidation on verdict | **Keep** — PRD §4.1, §9 item 18 |

## Summary

- **No redundant guards.** Every rule was added for a documented reason; each comment or PRD reference is precise.
- **One prune candidate:** `DELETE /api/models/{id}` (no UI, no test).
- **One rename suggestion:** `RecordTieAsync` → make it obvious that ELO does not move on the Tie path. (See suggestion in `RecordVerdictHandler` section above.)

> Net for this sweep: the design's invariants are intact. The next attacker isn't the bug they fixed; it's a future refactor that loses the no-evidence rule. The contract test in `PER-ROUTE-CONTRACT.md` is the better place to spend the next hour.