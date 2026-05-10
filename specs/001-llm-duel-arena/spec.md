# Feature Specification: PoLocalCompare — LLM Duel Arena

**Feature Branch**: `001-llm-duel-arena`
**Created**: 2026-05-09
**Status**: Draft
**Input**: User description: "PoLocalCompare: The Definitive Comprehensive Specification"

---

## Clarifications

### Session 2026-05-09

- Q: How do local models execute — in-browser via WebGPU/WebAssembly, or via a locally-installed server (e.g., Ollama)? → A: In-browser via WebGPU/WebAssembly (e.g., WebLLM). No extra local server is required; the user needs a WebGPU-capable browser.
- Q: Where is duel history, ELO records, and the Lab Archive persisted — browser storage or Azure Table Storage? → A: Azure Table Storage (server-side), per Constitution mandate. All persistence goes through the server API; data is shared across devices and sessions.
- Q: What does the user see during the silent processing phase (up to 5 minutes)? → A: Progress + stats — two per-model columns each showing an elapsed-time counter, a status label cycling through Initializing → Generating → Done/Failed, plus a live token count and estimated time-remaining updated as data becomes available. No generated code is shown.
- Q: What is the maximum acceptable latency between both models completing and the Arena viewports being visible? → A: Results must be visible within 1 second of both models completing (inferred default; user proceeded to planning).
- Q: How should remote model API providers be configured — fixed list (OpenAI, Anthropic) or user-defined endpoint + key? → A: Azure AI Foundry endpoints proxied via the .NET backend (inferred from technical stack provided by user).

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Configure and Launch a Duel (Priority: P1)

A user arrives at the War Room, selects one local browser-based model and one remote cloud model, types a prompt describing a "Simple Web App" they want built as a single-file HTML demo, and presses "Commence Duel." An audio cue fires immediately. The application runs both models concurrently in the background, enforcing a 5-minute hard time limit.

**Why this priority**: Without the ability to configure and launch a duel, nothing else in the application is reachable. This is the mandatory entry-point for all value the product delivers.

**Independent Test**: Can be fully tested by selecting two models, entering any prompt, pressing "Commence Duel," and verifying that (a) an audio cue plays, (b) the UI enters a "processing" state, and (c) both models are invoked within the time window.

**Acceptance Scenarios**:

1. **Given** the War Room is open and at least one local model and one remote model are available, **When** the user selects one of each and enters a non-empty prompt, **Then** the "Commence Duel" button becomes active.
2. **Given** the user presses "Commence Duel," **When** the action is confirmed, **Then** a snare-roll audio cue plays immediately and the UI transitions to a non-interactive "processing" state showing two side-by-side per-model panels, each with: an elapsed-time counter, a status label (Initializing → Generating → Done/Failed), a live token count, and an estimated time-remaining figure. No generated code is revealed during this phase.
3. **Given** a model is selected, **When** it is displayed in the model registry, **Then** its current ELO rating and the projected ELO gain/loss against the chosen opponent are both visible.
4. **Given** a model does not return a valid closing `</html>` tag within 5 minutes, **When** the timer expires, **Then** the model is marked "Failure," its partial output is preserved, and the Arena is still revealed.
5. **Given** the Pragmatism Constraint is active, **When** the system constructs the prompt, **Then** it automatically appends an instruction to use public CDNs for any external libraries.

---

### User Story 2 — Judge Results in the Arena (Priority: P2)

After both models finish (or time out), the Arena page reveals two sandboxed live viewports side-by-side (desktop) or stacked vertically (mobile). The user interacts with both generated apps, reads the telemetry HUD for each model, then clicks "Winner Left" or "Winner Right" to declare a verdict. A success audio cue plays, the losing viewport is visually dimmed, and ELO ratings update instantly.

**Why this priority**: The judging step is where the core value proposition is realized — the user compares real running apps and makes a data-informed decision.

**Independent Test**: Can be tested by injecting two pre-generated HTML outputs and verifying that both sandboxed viewports render, telemetry HUD data is displayed, clicking a winner plays audio and dims the loser, and ELO values change correctly.

**Acceptance Scenarios**:

1. **Given** both models have completed (or timed out), **When** the Arena loads, **Then** two isolated, interactive viewports are displayed — one per model — with no cross-contamination between them.
2. **Given** the Arena is visible, **When** the user reads a model's HUD, **Then** they can see: token velocity (tokens/sec), total generation time (seconds), energy cost (Wh and currency) for local models, API cost (currency) for remote models, and character density ratio.
3. **Given** a model was terminated by the 5-minute watchdog, **When** its viewport is displayed, **Then** it is labelled "Failure" and any partial output that was captured is rendered.
4. **Given** the user clicks "Winner Left" or "Winner Right," **When** the verdict is confirmed, **Then** the success audio cue plays, the losing viewport is visually desaturated/dimmed, and the ELO delta for both models is shown immediately.
5. **Given** the user clicks "Re-Challenge," **When** the action fires, **Then** the current result and ELO shift are saved, and the War Room reloads pre-filled with the same prompt and the same two models ready to run again.

---

### User Story 3 — Track and Compare Models on the Leaderboard (Priority: P3)

The user navigates to the Leaderboard to see every model ranked by ELO. They inspect performance sparklines showing rating trends over the last 20 matches, view a model's head-to-head "Kill List," and sort the table by "Green Score" (efficiency) rather than pure ELO.

**Why this priority**: The Leaderboard is the long-term retention mechanic and the source of truth for model quality; without it, individual duels lack persistent context.

**Independent Test**: Can be tested by seeding several historical duel records and verifying that models are ranked correctly by ELO, sparklines reflect the last 20 ratings, Kill List data is accurate, and Green Score sorting works independently of ELO order.

**Acceptance Scenarios**:

1. **Given** at least two models have completed duels, **When** the Leaderboard is viewed, **Then** models are listed in descending ELO order with their current rating, number of duels, and win/loss record.
2. **Given** a model has at least one historical rating data point, **When** its leaderboard row is displayed, **Then** a small sparkline trend graph shows rating evolution over its last 20 matches.
3. **Given** the user clicks on a model, **When** the detail view opens, **Then** a "Kill List" shows every opponent the model has faced, their ELO at the time, and the outcome (win/loss).
4. **Given** the leaderboard is displayed, **When** the user sorts by "Green Score," **Then** models are reordered by their average energy efficiency (logic output per watt-hour), independently of their ELO rank.

---

### User Story 4 — Browse Lab Archive and Export Reports (Priority: P4)

The user opens the Lab Archive, browses an append-only log of every historical duel (date, prompt, telemetry, verdict), revisits the generated apps from a past session, and exports a standalone HTML Lab Report for a specific duel to share externally.

**Why this priority**: The Archive enables long-term research value and professional shareability; it is not needed for first use but is essential for power users.

**Independent Test**: Can be tested by seeding five historical duel records and verifying that all entries appear in reverse chronological order, each entry shows all required metadata, past-generated apps can be re-rendered in an isolated viewport, and the exported Lab Report is a single self-contained HTML file containing all required content.

**Acceptance Scenarios**:

1. **Given** at least one completed duel exists, **When** the Lab Archive is opened, **Then** all duels appear in reverse chronological order with date, prompt summary, model names, and final verdict.
2. **Given** the user selects a historical duel, **When** the detail view opens, **Then** the full telemetry table for both models and the ELO shift from that session are displayed.
3. **Given** the user views a historical duel, **When** they click "Re-render," **Then** the previously generated HTML outputs are loaded into isolated sandboxed viewports so the user can interact with them again.
4. **Given** the user clicks "Export Lab Report" for a duel, **When** the export completes, **Then** a single, self-contained HTML file is downloaded containing: the raw prompt, the telemetry table, ELO shifts for both models, and the full source code for both the winner and loser.

---

### Edge Cases

- What happens when only one model completes before the 5-minute timer? (Remaining model is marked "Failure"; Arena still reveals both viewports.)
- What happens when the generated HTML is so large that it degrades the sandboxed viewport performance? (Render proceeds; character density metric flags oversized output.)
- What happens when a remote model's API returns an error mid-generation? (Model is marked "Failure" with error detail captured; duel continues with the other model.)
- What happens when the user has no local models downloaded? (War Room shows an empty "Local Combatants" section with guidance text; the Commence button remains disabled until a valid local model is available.)
- What happens when the ELO K-factor produces a sub-1-point shift? (ELO is still updated; displayed to one decimal place minimum.)

---

## Requirements *(mandatory)*

### Functional Requirements

<!--
  Constitution reminders:
  - SOLID/GoF pattern notes are required at implementation as code comments.
  - Feature flag: AI/LLM integration must be behind an appsettings toggle.
  - /diag and /health must reflect model connection status.
  - MOCK DATA banner required when AI calls are simulated.
  - ANON login path covers this app (no authentication required by this feature itself).
-->

**War Room — Model Configuration**

- **FR-001**: The application MUST display a dual-column registry listing available local models (in-browser WebGPU/WebAssembly) and available remote models (cloud API) separately.
- **FR-002**: Each model entry MUST display the model's current ELO rating and the projected ELO gain/loss for each potential opponent before a duel begins.
- **FR-003**: The user MUST be able to select exactly one local model and one remote model to form a duel pair.
- **FR-004**: The "Commence Duel" button MUST remain disabled until exactly one local model and one remote model are selected and the prompt text area is non-empty.
- **FR-005**: The system MUST automatically append a CDN pragmatism instruction to every prompt before sending it to any model, instructing models to use public CDN links for any external libraries.

**War Room — Prompt & Timer**

- **FR-006**: The application MUST provide a large text area for the user to input a "Simple Web App" requirement as the duel prompt.
- **FR-007**: The system MUST enforce a 5-minute hard time limit per model, beginning at the moment "Commence Duel" is pressed; this timer includes model initialization/warm-up time.
- **FR-008**: The system MUST display the 5-minute timer constraint in the War Room so users can verify it before commencing.

**Duel Mechanics — Watchdog & Generation**

- **FR-009**: The application MUST run both models concurrently once a duel commences; generated code is NOT streamed to the UI — the HTML output is revealed only when both models complete or the timer expires.
- **FR-009a**: During the processing phase, the UI MUST display two per-model panels, each showing: an elapsed-time counter, a status label cycling through `Initializing → Generating → Done / Failed`, a live token count, and an estimated time-remaining figure updated as inference progresses.
- **FR-010**: The system MUST track and record: model initialization (warm-up) duration, generation duration, and total tokens produced for each model.
- **FR-011**: If a model does not produce a closing `</html>` tag within 5 minutes, it MUST be marked as "Failure" and any partial output captured up to that point MUST be preserved.
- **FR-012**: An audio snare-roll cue MUST play immediately when the user clicks "Commence Duel."

**Arena — Dual Viewports**

- **FR-013**: Both generated HTML outputs MUST be rendered in isolated, sandboxed viewports that prevent cross-contamination and external resource leakage.
- **FR-014**: On desktop, viewports MUST be displayed side-by-side; on mobile, they MUST be displayed in a vertically swipeable stack.
- **FR-015**: Each viewport MUST label the model it displays, including a "Failure" badge and partial-output warning if the watchdog terminated that model.

**Arena — Telemetry HUD**

- **FR-016**: Each model's telemetry HUD MUST display: token velocity (tokens/sec), total generation time (seconds), character density ratio (functional characters / total file size).
- **FR-017**: For local models, the HUD MUST display estimated energy consumption in watt-hours and its equivalent financial cost.
- **FR-018**: For remote models, the HUD MUST display the estimated API financial cost based on token consumption.

**Arena — Verdict & ELO**

- **FR-019**: The user MUST be presented with "Winner Left," "Winner Right," and "Re-Challenge" actions upon duel completion.
- **FR-020**: Clicking "Winner Left" or "Winner Right" MUST immediately calculate and persist updated ELO ratings for both models using the standard Elo formula with configurable K-factor.
- **FR-021**: Upon verdict selection, a success audio cue MUST play and the losing viewport MUST be visually desaturated or dimmed.
- **FR-022**: Clicking "Re-Challenge" MUST save the current result and ELO updates, then immediately present the War Room pre-filled with the same prompt and the same two models.

**Leaderboard**

- **FR-023**: The Leaderboard MUST display all models that have participated in at least one duel, ranked by current ELO in descending order.
- **FR-024**: Each leaderboard row MUST include a sparkline trend graph reflecting the model's ELO trajectory over its last 20 duels.
- **FR-025**: Clicking a model on the Leaderboard MUST open a detail view showing its head-to-head history ("Kill List") with outcomes against every opponent it has faced.
- **FR-026**: The Leaderboard MUST be sortable by "Green Score" (average logic output per watt-hour) as an alternative ranking to ELO.

**Lab Archive**

- **FR-027**: Every completed duel MUST be appended to an immutable Lab Archive log stored in Azure Table Storage; existing duel records MUST NOT be editable or deletable through the UI.
- **FR-028**: The Lab Archive MUST display duels in reverse chronological order, showing: date, prompt summary, participating model names, and final verdict.
- **FR-029**: The user MUST be able to select any archived duel and re-render both models' HTML outputs in isolated sandboxed viewports.
- **FR-030**: The user MUST be able to export any archived duel as a single, self-contained HTML Lab Report file containing: raw prompt, full telemetry table, ELO shifts, and complete source code for both models.

**Observability & Diagnostics (Constitution IV)**

- **FR-031**: The `/diag` page MUST display the connection status of the Azure Table Storage instance, all configured remote model API endpoints, and the WebGPU availability status reported by the client, masking middle characters of any API keys shown.
- **FR-032**: The `/health` endpoint MUST return a JSON response reporting the status of each configured model source.

**Visual Theme**

- **FR-033**: The application MUST use an OLED Black (`#000000`) background with a restricted functional color palette: white/light grey for neutral text, green for success/high performance, yellow for warning/average performance, red for failure/high cost.
- **FR-034**: If AI-generated data is mocked (test/integration environments), a prominent "MOCK DATA" banner MUST be displayed at the top of every affected page.

### Key Entities

- **Model**: Represents a registered LLM (local or remote); holds display name, type (local/remote), current ELO rating, and — for local models — the WebGPU/WebAssembly model identifier and estimated watt draw; for remote models — the API provider endpoint and token pricing metadata.
- **Duel**: A single benchmarking session; holds the prompt, references to both competing models, start/end timestamps, telemetry for each participant, and the final verdict.
- **DuelResult**: The per-model outcome within a Duel; holds generation duration, warm-up duration, token count, token velocity, energy cost (local), API cost (remote), character density, success/failure status, and raw HTML output. Persisted to Azure Table Storage.
- **EloRecord**: An immutable snapshot of a model's ELO rating after each duel; used to build sparklines and trend graphs. Persisted to Azure Table Storage (append-only).
- **HeadToHead**: A derived record of outcomes between two specific models across all duels; used for the Kill List.
- **LabReport**: A self-contained, exportable artifact derived from a completed Duel, formatted as a standalone HTML file.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can configure a duel (select models, enter prompt) and press "Commence Duel" in under 60 seconds from opening the War Room.
- **SC-002**: Both model results are revealed simultaneously once generation completes (or the 5-minute timer expires); the Arena viewports MUST be visible and interactive within 1 second of both models finishing.
- **SC-003**: ELO ratings for both models update and are visible on the Leaderboard within 3 seconds of the user selecting a winner.
- **SC-004**: The telemetry HUD displays token velocity, energy cost (local), and API cost (remote) for 100% of completed duels.
- **SC-005**: A Lab Report export produces a single, self-contained HTML file that opens and renders correctly offline in a standard browser without any external network requests.
- **SC-006**: The Arena viewport layout adapts to desktop (side-by-side) and mobile (vertical stack) with no loss of telemetry or verdict functionality.
- **SC-007**: 100% of duels where a model exceeds the 5-minute limit are recorded with "Failure" status and preserved partial output, without crashing or hanging the application.
- **SC-008**: The "Green Score" leaderboard sort correctly reorders models by energy efficiency (logic-per-watt), independent of ELO rank, for any dataset of 2 or more models.

---

## Assumptions

- Local models run entirely inside the browser via WebGPU/WebAssembly (e.g., WebLLM); no separate local server or desktop process is required. The user must have a WebGPU-capable browser (e.g., Chrome 113+).
- Users have at least one compatible local model pre-loaded and available in their browser environment before using the application; the app does not handle model downloading.
- Remote model connections require valid API credentials provided by the user via configuration; the app does not manage API account creation.
- Energy cost estimates for local models are calculated using a configurable watt assumption for the user's device class (default: a reasonable mid-range desktop figure); users can override this value in settings.
- Financial cost calculations for electricity use a configurable cost-per-kWh value (default: a broadly representative rate); users can override this.
- The ELO K-factor defaults to 32 (standard chess rapid rating); it is configurable via application settings without code deployment (feature flag / appsettings toggle).
- Prompt content is the user's sole responsibility; the application appends only the CDN pragmatism instruction and does not filter or modify the user's text otherwise.
- Audio cues are supported by the host browser; if audio is unavailable (permissions denied), the application proceeds silently without error.
- All duel history, ELO records, and the Lab Archive are persisted to Azure Table Storage in the application's own resource group (`PoLocalCompare-rg`), accessed via the server API. No browser-side persistence (IndexedDB, localStorage) is used for permanent data.
- Mobile support targets current-generation smartphones in portrait orientation; tablet and landscape-mobile layouts are best-effort in v1.
- Authentication is not required for this application; any user who opens the app has full access (ANON login path is not needed for this feature).
