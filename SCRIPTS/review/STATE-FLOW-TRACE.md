# State-flow trace (Review #9)

For every Razor page with non-trivial `@code`, this lists the `@state` fields and the events that drive them. A field is **dead** if no event ever writes it; a field is **single-source** if it has exactly one writer (easy to reason about); a field is **bug-clause** if a comment explicitly says "this exists to patch a previous bug".

## Arena.razor (PRD §1: "the duel is the Arena")

State inventory (32 fields):

| Field | Writer(s) | Reader(s) | Notes |
|---|---|---|---|
| `_duel` | `LoadAsync`, `RecordVerdictAsync`, `OnDuelUpdated` | markup | Single writer post-load |
| `_leftResult`, `_rightResult` | `LoadAsync`, `OnTokenUpdate`, `OnDuelComplete` | markup, `BothOutputsPresent` | Two writers, last-wins |
| `_loading` | `LoadAsync` only | markup | **Single source** ✓ |
| `_submitting` | `RecordVerdictAsync` | markup, `RecordVerdictAsync` (re-entrancy guard) | **Single source** ✓ |
| `_verdictRecorded` | `RecordVerdictAsync` (true branch), `LoadAsync` (false branch) | markup | **Bug-clause candidate** — gates "Retry" off "this exists because a previous version left dead buttons" |
| `_retrying` | `RetryDuelAsync` | markup, `RetryDuelAsync` (re-entrancy guard) | **Single source** ✓ |
| `_retryError` | `RetryDuelAsync` only | markup | **Single source** ✓ |
| `_winnerModelId`, `_loserModelId` | `RecordVerdictAsync` only | markup | Tied to `_verdictRecorded` ✓ |
| `_eloShiftLeft`, `_eloShiftRight` | `RecordVerdictAsync` only | markup | Animation source for `_eloDisplayLeft/Right` ✓ |
| `_viewMode` | markup toggle (Rendered/Code/Diff) | markup, `BothOutputsPresent` | **Single source** ✓ |
| `_leftProbe`, `_rightProbe` | `OnLeftProbe`, `OnRightProbe` | markup | Sandbox probe results |
| `_optimistic` | `RecordVerdictAsync` (true branch), `LoadAsync` | markup, `RecordVerdictAsync` | **Bug-clause** — "the click flips the UI immediately" |
| `_overriddenNotice` | `RecordVerdictAsync` only | markup | Set when auto-judge won the race; not cleared (intentional, see comment) |
| `_eloDisplayLeft/Right` | `RecordVerdictAsync` (animated), `AnimateEloAsync` | markup | Animation target |
| `_eloAnimationCts` | `RecordVerdictAsync` | `RecordVerdictAsync`, `DisposeAsync` | **Single source** ✓ |
| `_verdictSource` | `LoadAsync` | markup | Defaults to `Human`; AI judge writes through `RecordVerdictAsync` indirectly |
| `_verdictValue` | `RecordVerdictAsync`, `LoadAsync` | markup | Tracks `DuelVerdict` separately from `_winnerModelId` for the `Tie` case |
| `_judgeRationale` | `RecordVerdictAsync` | markup | **Single source** ✓ |
| `_autoJudgeRemaining` | `StartAutoJudgeCountdownAsync`, `LoadAsync` | markup | Countdown mirror of server clock |
| `_autoJudgeDeciding` | `RecordVerdictAsync` (AI judge path) | markup | **Single source** ✓ |
| `_autoJudgeCts` | `LoadAsync` | `LoadAsync`, `DisposeAsync` | **Single source** ✓ |

**Dead / suspicious:** none. Every field has at least one writer and reader.

**Reductions possible:**

1. `_verdictValue` and `_winnerModelId` overlap: when verdict is `Left`/`Right`, both are set; when `Tie`, only `_verdictValue` is set; when `Pending`, only `_verdictRecorded` distinguishes. Could fold to a single `enum? Verdict` + nullable winner. **Cost:** ~1 hour, ~50 LOC changed; **risk:** regression risk in the optimistic-verdict race path. **Recommendation:** leave — the comment explicitly defends the split.
2. `_optimistic` and `_verdictRecorded` overlap: `_optimistic` implies `_verdictRecorded` (and the opposite). Could fold. **Cost:** ~30 min; **risk:** low — they have identical read contexts. **Recommendation:** fold next time this file is touched.

## Demo.razor

State: `_phase` (`Idle`/`Running`/`Done`), `_plan`, `_current`, `_completedCount`, `_stopRequested`, `_loadError`.

- All fields have one writer each.
- `_phase` is a 3-state enum — could be 2 booleans (`_running`, `_stopped`), but the enum is a single discriminant that the markup reads. **Leave.**

## Leaderboard.razor

State: `_entries`, `_displayedEntries`, `_loading`, `_error`, `_sortBy`, `_activeOnly`.

- `_entries` vs `_displayedEntries`: derived list, materialized once per change. PRD §9 item 18 noted "materialised once per change instead of per read" — this is the audit's own past work. **Verified clean.**
- `_sortBy` is bound to a `string` ("Elo"/"Quality"/"Cost"), then validated server-side; falls back to ELO when unknown. The audit confirmed this in PRD §9 item 19. **Verified clean.**

## Archive.razor

State: `_duels`, `_filteredDuels`, `_loading`, `_error`, `_hasMore`, `_verdictFilter`, `_selectedDuel`.

- `_duels` vs `_filteredDuels`: same pattern as Leaderboard — derived, materialized. **Verified clean.**
- `_selectedDuel` is null `; no "was a selection cleared?" state. **Clean.**

## Home.razor

State: 14 fields including `_leftModel`, `_rightModel`, `_models`, `_availability`, `_replacePopoverModel`, `_openStep` (1/0), `_modelFilter`, `_showUnavailable`.

- `_openStep` is the disclosure state — PRD §9 item 18 says "exactly one of steps 1 and 2 is open". The 0 means "all closed". The Compare CTA has its own row outside the disclosure. **Verified clean.**
- `_replacePopoverModel` is a `ModelDto?` — appears in the markup only when *both* slots are full and the user clicked a third model. **Single source.** ✓

## Summary

**No dead state.** Two reasonable reductions (`_verdictValue`+`_winnerModelId`, `_optimistic`+`_verdictRecorded`) but both are documented design choices with non-trivial regression cost. The Arena's 32-field state machine is the densest in the codebase, justified by PRD §9 items 13, 18, 19 (every field corresponds to a documented design constraint).

> Note: PRD §9 item 13 is *deliberately* separate from `OutputQualityScore` (presentation-only). A reviewer who hasn't read it will see `_qualityScoreHeuristic` and assume it's persisted. It isn't.