# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]
**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

[Extract from feature spec: primary requirement + technical approach from research]

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: C# 14 / .NET 10 (pinned via global.json)
**Primary Dependencies**: ASP.NET Core, Blazor WASM (hosted), Radzen UI, Serilog, OpenTelemetry, Testcontainers
**Storage**: Azure Table Storage (Azurite in Docker for local dev)
**Testing**: xUnit (unit/integration), Playwright/TypeScript (E2E headed)
**Target Platform**: Azure App Services (server) + Blazor WASM (client, hosted in server)
**Project Type**: Client/Server web application — Onion Architecture server, simple Blazor WASM client
**Performance Goals**: [domain-specific, e.g., sub-200ms p95 API responses or NEEDS CLARIFICATION]
**Constraints**: HTTP 5000 / HTTPS 5001 local; no secrets in appsettings; TreatWarningsAsErrors; Nullable enabled
**Scale/Scope**: [domain-specific, e.g., small team internal tool or NEEDS CLARIFICATION]

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify ALL of the following before proceeding:

- [ ] **I. Naming**: Solution/project names carry `Po` prefix; namespaces use `PoLocalCompare.*`; `global.json` pins .NET 10.
- [ ] **II. Architecture**: Server uses Onion Architecture (Domain → Application → Infrastructure physical separation); client stays simple; Blazor WASM + Radzen confirmed; SOLID/GoF pattern comments planned.
- [ ] **III. Structure**: `Directory.Packages.props` + `Directory.Build.props` in root; `PoLocalCompare.Shared` planned; `wwwroot` only in client; `src/` + `tests/` layout; `.gitignore` up to date.
- [ ] **IV. API Standards**: Ports fixed (HTTP 5000 / HTTPS 5001); Scalar/OpenAPI + `.http` files planned; `/diag` and `/health` endpoints included in scope.
- [ ] **V. Azure/Secrets**: No secrets in `appsettings.json`; Key Vault + Managed Identity planned; App Service Plan references `PoShared` RG; Table Storage in app's own RG.
- [ ] **VI. Auth/Security**: ANON login button included (if auth used); OWASP Top 10 addressed; Microsoft OAuth in dev and prod.
- [ ] **VII. Testing**: Unit tests (Domain/Application), Integration tests (Testcontainers/Azurite), E2E Playwright (headed); mock data for AI in test contexts; `MOCK DATA` banner planned.
- [ ] **VIII. Observability**: Serilog (File + Console + App Insights); OpenTelemetry to PoShared; `UserId`/`SessionId`/`CorrelationId`/`Environment` in log context; dev-mode stack traces in UI.
- [ ] **IX. Hygiene**: No dead code; feature flags for external integrations; `/LLMDOCS` updated; ambiguity stop-rule applied.
- [ ] **X. DX**: F5 kills existing processes and opens Edge; Scalar + `.http` maintained; `/diag` current.

*Any unchecked item MUST be addressed or justified in the Complexity Tracking table before implementation begins.*

## Project Structure

### Documentation (this feature)

```text
specs/[###-feature]/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)
<!--
  ACTION REQUIRED: Replace the placeholder tree below with the concrete layout
  for this feature. Delete unused options and expand the chosen structure with
  real paths (e.g., apps/admin, packages/something). The delivered plan must
  not include Option labels.
-->

```text
# PoLocalCompare — standard layout (Constitution § III)
src/
├── PoLocalCompare.Domain/          # Entities, value objects, domain services (no external deps)
├── PoLocalCompare.Application/     # Use cases, interfaces, DTOs (depends on Domain only)
├── PoLocalCompare.Infrastructure/  # EF/Table Storage, Key Vault, external APIs (depends on Application)
├── PoLocalCompare.Api/             # ASP.NET Core host; serves WASM; ports 5000/5001
└── PoLocalCompare.Shared/          # DTOs & contracts shared between Api and Client

src/Client/
└── PoLocalCompare.Client/          # Blazor WASM; wwwroot here only; Radzen components

tests/
├── unit/
│   └── PoLocalCompare.Unit.Tests/
├── integration/
│   └── PoLocalCompare.Integration.Tests/   # Testcontainers (Azurite, SQL)
└── e2e/
    └── PoLocalCompare.E2E/                 # Playwright / TypeScript (headed in Dev)

/LLMDOCS/                           # LLM quick-reference docs (kept current)
global.json                         # Pins .NET 10 SDK
Directory.Build.props               # TreatWarningsAsErrors, Nullable
Directory.Packages.props            # Central Package Management
```

**Structure Decision**: Onion Architecture server (Domain/Application/Infrastructure/Api) + hosted Blazor WASM client. All source under `src/`, all tests under `tests/`.

[REMOVE IF UNUSED — Options 2 and 3 do not apply to this project]

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
