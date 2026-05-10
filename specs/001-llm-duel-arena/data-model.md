# Data Model: PoLocalCompare — LLM Duel Arena

**Phase**: 1 — Design
**Date**: 2026-05-09
**Source**: research.md §5, spec.md Key Entities

---

## Entities & Azure Table Storage Mapping

### 1. Model

Represents a registered LLM available in the duel registry.

| Field | Type | Notes |
|-------|------|-------|
| `ModelId` | `string` (ULID) | RowKey |
| `DisplayName` | `string` | Human-readable name, e.g., "Gemma 4 (Local)" |
| `ModelType` | `enum` `Local \| Remote` | Determines HUD display (energy vs API cost) |
| `CurrentElo` | `double` | Mutable; updated after each duel verdict |
| `DuelCount` | `int` | Total duels participated in |
| `WinCount` | `int` | Total wins |
| `GreenScoreAvg` | `double` | Running average tokens/Wh; updated after each local duel |
| `TdpWatts` | `double?` | Local models only; GPU TDP used for energy estimation |
| `ApiEndpointRef` | `string?` | Remote models only; Azure AI Foundry deployment name |
| `WebLlmModelId` | `string?` | Local models only; e.g., `"gemma-4-it-q4f32_1"` |
| `CreatedAt` | `DateTimeOffset` | Registration timestamp |

**Table**: `Models`
**PartitionKey**: `"model"` (single partition; model count expected to be small)
**RowKey**: `ModelId`

**Validation rules**:
- `DisplayName`: required, 1–100 characters.
- `TdpWatts`: required when `ModelType = Local`; must be > 0.
- `ApiEndpointRef`: required when `ModelType = Remote`.
- `WebLlmModelId`: required when `ModelType = Local`.
- `CurrentElo`: initialised to 1200 (standard chess starting rating) on registration.
- `DuelCount`, `WinCount`: non-negative integers; `WinCount ≤ DuelCount`.

---

### 2. Duel

A single benchmarking session between two models.

| Field | Type | Notes |
|-------|------|-------|
| `DuelId` | `string` (ULID) | RowKey; ULID provides time-ordering |
| `PromptText` | `string` | Raw user prompt (before CDN instruction appended) |
| `PromptFull` | `string` | Full prompt as sent to models (includes CDN suffix) |
| `LeftModelId` | `string` | FK → Model |
| `RightModelId` | `string` | FK → Model |
| `StartedAt` | `DateTimeOffset` | When "Commence Duel" was pressed |
| `CompletedAt` | `DateTimeOffset?` | When both models finished or timer expired |
| `Verdict` | `enum` `Left \| Right \| Pending` | Set when user clicks Winner |
| `WinnerModelId` | `string?` | Set on verdict; null while Pending |
| `LoserModelId` | `string?` | Set on verdict; null while Pending |
| `EloShiftWinner` | `double?` | Points gained by winner |
| `EloShiftLoser` | `double?` | Points lost (negative) by loser |

**Table**: `Duels`
**PartitionKey**: `YYYYMM` (month bucket — e.g., `"202605"`) for efficient recent-history queries
**RowKey**: `DuelId` (ULID — lexicographically ordered by creation time)

**Validation rules**:
- `LeftModelId ≠ RightModelId` — a model cannot duel itself.
- `PromptText`: required, non-empty.
- `Verdict` starts as `Pending`; transitions to `Left` or `Right` exactly once (immutable after set).
- `CompletedAt` ≥ `StartedAt`.

**State transitions**:
```
Pending → (user selects verdict) → Left | Right   [terminal, immutable]
```

---

### 3. DuelResult

Per-model outcome record within a Duel. Two DuelResult rows exist per Duel (one per model).

| Field | Type | Notes |
|-------|------|-------|
| `DuelId` | `string` | PartitionKey |
| `ModelId` | `string` | RowKey |
| `WarmUpDurationMs` | `long` | Time from start to first token (ms) |
| `GenerationDurationMs` | `long` | Time from first token to completion/timeout (ms) |
| `TotalDurationMs` | `long` | WarmUp + Generation |
| `TokenCount` | `int` | Total tokens generated |
| `TokenVelocity` | `double` | Tokens/sec (TokenCount / (GenerationDurationMs / 1000)) |
| `HtmlOutputRaw` | `string` | Full HTML output (or partial if watchdog fired) |
| `HtmlOutputSizeBytes` | `long` | Byte length of HtmlOutputRaw |
| `CharacterDensityRatio` | `double` | Functional chars / total chars (see note) |
| `IsFailure` | `bool` | True if watchdog terminated before `</html>` |
| `FailureReason` | `string?` | "Timeout" \| "ApiError: {msg}" \| null |
| `EnergyWh` | `double?` | Local models: TdpWatts × TotalDurationMs / 3_600_000 |
| `EnergyCostUsd` | `double?` | Local: EnergyWh / 1000 × kWhRateUsd |
| `ApiCostUsd` | `double?` | Remote: calculated from token counts × pricing |
| `GreenScore` | `double?` | TokenCount / EnergyWh; null for remote models |

**Table**: `DuelResults`
**PartitionKey**: `DuelId`
**RowKey**: `ModelId`

**Notes**:
- `CharacterDensityRatio`: "functional characters" = non-whitespace, non-comment characters in the HTML output. Calculated server-side by stripping HTML comments and collapsing whitespace, then dividing by `HtmlOutputSizeBytes`.
- `HtmlOutputRaw` may exceed Table Storage's 64KB property limit for large outputs. If so, content is stored in Azure Blob Storage and `HtmlOutputRaw` contains the blob URI (prefixed `blob://`). This is transparent to callers.

---

### 4. EloHistory

Immutable ELO snapshot after each duel verdict. Used for sparklines and trend graphs.

| Field | Type | Notes |
|-------|------|-------|
| `ModelId` | `string` | PartitionKey |
| `TimestampKey` | `string` | RowKey = `{invertedTicks}_{DuelId}` for descending order |
| `DuelId` | `string` | FK → Duel |
| `EloAfter` | `double` | Model's ELO immediately after this duel |
| `EloBefore` | `double` | Model's ELO before this duel |
| `EloShift` | `double` | EloAfter − EloBefore (positive = gained) |
| `Outcome` | `enum` `Win \| Loss` | |
| `OpponentModelId` | `string` | FK → Model |
| `OpponentEloBefore` | `double` | Opponent's ELO at time of duel (for expected score calc) |
| `RecordedAt` | `DateTimeOffset` | Verdict timestamp |

**Table**: `EloHistory`
**PartitionKey**: `ModelId`
**RowKey**: `{invertedTicks}_{DuelId}` — inverted ticks (`long.MaxValue - DateTimeOffset.UtcNow.Ticks`) gives natural descending order so "top 20 by RowKey" = most recent 20.

**Notes**:
- Records are append-only. No updates or deletes.
- Sparkline data = top 20 rows for a given `ModelId` (ascending `EloAfter` sequence after reversing the inverted-ticks order).
- Kill List = all rows for a given `ModelId`, grouped by `OpponentModelId`, aggregated to win/loss counts.

---

### 5. HeadToHead (derived / in-memory)

Not stored as a separate table. Computed on demand by aggregating `EloHistory` rows for a given `ModelId`, grouped by `OpponentModelId`.

| Field | Type | Notes |
|-------|------|-------|
| `ModelId` | `string` | Subject model |
| `OpponentModelId` | `string` | |
| `Wins` | `int` | Count of Win outcomes vs this opponent |
| `Losses` | `int` | Count of Loss outcomes vs this opponent |
| `LastDuelId` | `string` | Most recent duel between these two |

---

### 6. LabReport (derived / exported artifact)

Not stored. Generated on-demand by the server's Razor-to-HTML engine from Duel + DuelResult + EloHistory data.

**Content** (per FR-030):
- Raw prompt (`PromptText`)
- Full telemetry table (all DuelResult fields for both models)
- ELO shifts (`EloShiftWinner`, `EloShiftLoser`, ratings before/after)
- Full source code of both models' HTML outputs (embedded inline)
- Self-contained: all CSS inlined, no external requests

---

## Entity Relationship Summary

```
Model (1) ──── (many) EloHistory
Model (1) ──── (many) DuelResult
Duel  (1) ──── (2)    DuelResult
Duel  (1) ──── (many) EloHistory (one per participating model per duel)

HeadToHead ← derived from EloHistory
LabReport  ← derived from Duel + DuelResult + EloHistory
```

---

## Configuration Entities (appsettings.json / Key Vault)

These are not stored in Table Storage but govern runtime behaviour:

| Key | Default | Description |
|-----|---------|-------------|
| `Elo:KFactor` | `32` | ELO sensitivity factor |
| `Elo:StartingRating` | `1200` | Initial rating for new models |
| `GreenStats:DefaultTdpWatts` | `115.0` | RTX 5070 Ti TGP (configurable) |
| `GreenStats:ElectricityRateUsd` | `0.15` | USD per kWh |
| `Duel:TimeLimitSeconds` | `300` | Watchdog hard limit (5 minutes) |
| `Features:UseRealAi` | `true` in Dev, `false` in Integration/Test | AI call feature flag |
| `AzureAiFoundry:Endpoint` | Key Vault | Foundry endpoint URL |
| `AzureAiFoundry:DeploymentName` | Key Vault | Default deployment |
