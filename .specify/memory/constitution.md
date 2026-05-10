<!--
SYNC IMPACT REPORT
==================
Version change: (unfilled template) → 1.0.0
Added principles:
  - I. Project Identity & Naming Standards (new)
  - II. Core Architecture & Frameworks (new)
  - III. Project Structure & Configuration (new)
  - IV. API & Backend Standards (new)
  - V. Infrastructure & Azure Deployment (new)
  - VI. Authentication & Security (new)
  - VII. Mandatory Testing & Quality Assurance (new)
  - VIII. Observability & Debugging (new)
  - IX. Engineering Hygiene & LLM Workflow (new)
  - X. Developer Experience & Tooling (new)
Added sections:
  - Core Principles (10 principles)
  - Infrastructure & Azure Standards
  - Development Workflow & Quality Gates
  - Governance
Templates requiring updates:
  - .specify/templates/plan-template.md ✅ updated (Constitution Check gates added)
  - .specify/templates/spec-template.md ✅ updated (SOLID/GoF annotation note added)
  - .specify/templates/tasks-template.md ✅ updated (path conventions confirmed: src/ + tests/)
Deferred TODOs: none
-->

# PoLocalCompare Constitution

## Core Principles

### I. Project Identity & Naming Standards

- The solution file MUST be named `PoLocalCompare.sln` and every project title MUST carry the `Po` prefix (e.g., `PoLocalCompare.Api`, `PoLocalCompare.Domain`).
- The master prefix `PoLocalCompare` MUST be applied consistently to all C# namespaces, Azure Resource Groups, and Aspire resource names.
- A `global.json` file MUST exist at the repository root and pin the SDK to the latest stable .NET 10 release. No implicit SDK version floating is permitted.
- Any deviation in naming (missing prefix, wrong casing) MUST be treated as a build-breaking violation.

### II. Core Architecture & Frameworks

- The server-side project MUST implement strict **Onion Architecture** with physically separate assemblies for `Domain`, `Application`, and `Infrastructure` layers. The `Domain` layer MUST have zero dependencies on `Infrastructure`. Reference: https://blog.anilgurau.com/step-by-step-approach-to-use-onion-architecture-in-net
- The client-side (Blazor WASM) project MUST be kept simple — no Onion layering inside the client; only UI components and service proxies.
- The UI framework MUST use **Blazor WASM** hosted within the server project. **Radzen** UI components MUST be used for data grids, forms, and complex controls.
- All C# code MUST target **C# 14** language features where appropriate.
- **SOLID** and **GoF** design patterns MUST be applied deliberately. Every non-trivial application of a pattern MUST include a code comment identifying the pattern (e.g., `// SOLID: Dependency Inversion`, `// GoF: Repository pattern`).

### III. Project Structure & Configuration

- **Centralized Package Management**: `Directory.Packages.props` MUST exist at the root and manage all NuGet package versions centrally. No version attributes are permitted inside individual `.csproj` files.
- **Build Strictness**: `Directory.Build.props` at the root MUST set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<Nullable>enable</Nullable>`. Zero warnings are permitted in CI.
- **Shared Logic**: A `PoLocalCompare.Shared.csproj` MUST hold all DTOs, contracts, and types shared between client (WASM) and server (API). This project MUST NOT reference server-only or client-only frameworks.
- **Blazor Hosting**: The Blazor WASM app MUST be hosted within the server project. Server ports are fixed: HTTP `5000`, HTTPS `5001`.
- **Folder Hygiene**: The `wwwroot` folder MUST exist only in the client (WASM) project and MUST be deleted from the server project if present. The `.gitignore` MUST cover `.vs/`, `.vscode/`, `bin/`, `obj/`, `*.user`, `*.suo`, and all standard .NET build artifacts.
- **Source layout**: All source code MUST reside under `src/`. All test projects (unit, integration, E2E) MUST reside under `tests/`.

### IV. API & Backend Standards

- **Fixed Local Ports**: All local development configurations MUST bind HTTP to `5000` and HTTPS to `5001`. Randomized ports are prohibited in `launchSettings.json`.
- **OpenAPI (Scalar)**: MUST be enabled on every API project. `.http` files MUST be provided for all primary endpoints to support direct request-level debugging.
- **Diagnostics Page (`/diag`)**: MUST display the live status of all external connections (databases, APIs, Key Vault), all configuration keys in use (masking the middle characters of any sensitive value), and any other integration points that benefit from real-time health verification.
- **Health Endpoint (`/health`)**: MUST return a valid JSON response that pings each external dependency and reports its status. This endpoint MUST be accessible without authentication.
- **VS Code F5 Launch**: The F5 task MUST kill all existing dotnet processes before launching the server (or Aspire dashboard) and MUST open the application in the Edge browser automatically.

### V. Infrastructure & Azure Deployment

- **No Secrets in Config**: Secrets MUST NEVER be stored in `appsettings.json` or any checked-in file. All secrets MUST be sourced from Azure Key Vault (the shared `PoShared` Key Vault).
- **Secret Naming Convention**: App-specific secrets (OAuth tokens, Storage connection strings) MUST be prefixed with the app name (e.g., `PoLocalCompare-StorageConnectionString`). Shared secrets (telemetry keys, shared service endpoints) MUST remain un-prefixed.
- **Cloud Identity**: Managed Identity MUST be used for all Azure resource access within subscription `Punkouter26` (ID: `Bbb8dfbe-9169-432f-9b7a-fbf861b51037`). No service principal passwords or connection string credentials are permitted in production.
- **Azure Resources**: The primary deployment targets are **Azure App Services** and **Azure Table Storage**. App Service Plans MUST reference plans that exist in the `PoShared` resource group. Azure Table Storage MUST be provisioned in the application's own resource group — not a shared one.

### VI. Authentication & Security

- **ANON Login (Dev/Testing)**: If the application uses authentication, an "RANDOM ANON" button MUST be implemented to bypass OAuth during local development and E2E testing. This path MUST NOT be available in production.
  - The ANON user name MUST include a random numeric suffix (e.g., `ANON463443`) so each ANON session is unique.
  - All activity by ANON users MUST be persisted to the database under the ANON account identifier.
  - When an ANON user is active, the navbar MUST display `ANON LOGGED IN`. When an authenticated user is active, the navbar MUST display their email.
- **OAuth**: Microsoft OAuth MUST be supported in both development and production environments.
- **OWASP Top 10**: All code MUST be free of the OWASP Top 10 vulnerabilities. Security issues discovered during development or review MUST be fixed immediately before work continues.

### VII. Mandatory Testing & Quality Assurance

- **Unit Tests (C#)**: MUST cover Domain logic and Application Service layers. Test projects live under `tests/unit/`.
- **Integration Tests (C#)**: MUST use **Testcontainers** (Azurite for Table Storage, SQL containers where applicable) to test API endpoints and repository patterns end-to-end. Test projects live under `tests/integration/`.
- **E2E Tests (TypeScript/Playwright)**: MUST cover critical user paths in the Blazor UI and MUST run in headed mode during local development. Test projects live under `tests/e2e/`.
- **Local Storage Simulation**: Local development MUST use **Azurite running in Docker** for Table Storage simulation. No local storage emulators or direct Azure connections are permitted for dev/test runs.
- **AI Integration Testing**: Real AI service calls are permitted ONLY when running the application locally as an end-user (DEV mode, `ASPNETCORE_ENVIRONMENT=Development`). Integration and E2E tests MUST use mock data resembling real AI responses. When running manually (local or Azure), real services MUST be used — mock data is prohibited in that context.
- **Mock Data Transparency**: Any page or view rendering mock data MUST display a prominent `MOCK DATA` banner at the top of the page.

### VIII. Observability & Debugging

- **Structured Logging**: **Serilog** MUST be configured to write to File, Console, and Azure Application Insights sinks simultaneously.
- **OpenTelemetry**: MUST be enabled globally and aggregated to the `PoShared` App Insights resource.
- **Required Log Context**: Every log entry MUST include `UserId`, `SessionId`, `Environment`, `CorrelationId`, and full `Exception` objects (including stack traces) as structured properties.
- **Dev UI Transparency**: In `Development` mode, the UI MUST surface specific error messages and full stack traces to facilitate rapid debugging. This information MUST NOT be surfaced in production.

### IX. Engineering Hygiene & LLM Workflow

- **Zero-Waste Policy**: Unused files, dead code, commented-out blocks, and obsolete assets MUST be deleted immediately when discovered. They MUST NOT be left for "later cleanup."
- **Code Documentation**: Comments are REQUIRED for complex business logic and for every SOLID/GoF pattern application. Comments are PROHIBITED on self-explanatory code (e.g., standard constructors, simple property assignments).
- **Feature Flags**: External API integrations and experimental features MUST be gated by `appsettings` toggles to allow behavior changes without code deployment.
- **Ambiguity Stop-Rule**: If a task or requirement is unclear, work MUST STOP. A bulleted list of assumptions MUST be presented to the user for clarification before any code is generated.
- **LLM Documentation (`/LLMDOCS`)**: A `/LLMDOCS` folder MUST be maintained at the repository root. It MUST be updated whenever project structure or public API surfaces change significantly. It MUST contain concise, accurate information that enables a coding LLM to quickly understand the codebase.

### X. Developer Experience & Tooling

- The F5 / run experience MUST be frictionless: kill existing processes → launch → open browser, all in one step.
- OpenAPI (Scalar) and `.http` files MUST be maintained alongside every API change so developers can exercise endpoints without external tooling.
- The `/diag` page MUST be the first place a developer looks when something breaks — it MUST be kept accurate and up to date.
- `LLMDOCS` MUST be a living document; stale docs are treated the same as dead code — a violation subject to the Zero-Waste Policy.

## Infrastructure & Azure Standards

- All Azure resources MUST be deployed via Infrastructure as Code (Bicep or azd) stored in the repository. No manual portal-only provisioning is accepted.
- Resource naming MUST follow the `PoLocalCompare-{ResourceType}-{Environment}` convention.
- App Service Plans in `PoShared` resource group MUST be referenced (not recreated) by this application's deployment.
- Azure Table Storage MUST reside in the application's own resource group (`PoLocalCompare-rg`), distinct from `PoShared`.
- Key Vault access MUST use Managed Identity role assignments — no access policies or connection string secrets for Key Vault itself.

## Development Workflow & Quality Gates

- **Branch Strategy**: All feature work MUST be done on feature branches following the `###-feature-name` convention. Direct commits to `main` are prohibited.
- **Quality Gate — Pre-merge**: All of the following MUST pass before a branch merges: zero compiler warnings (`TreatWarningsAsErrors`), unit tests green, integration tests green, Playwright E2E smoke pass.
- **Constitution Check (per plan)**: Every implementation plan MUST include a Constitution Check section verifying compliance with principles I through X before Phase 0 research begins.
- **No Broken Windows**: Any violation discovered during development (naming mismatch, missing test, committed secret, dead code) MUST be remediated in the same PR — not deferred.
- **Dependency Updates**: NuGet dependencies MUST be managed exclusively through `Directory.Packages.props`. No ad-hoc package version overrides in individual projects.

## Governance

This constitution supersedes all other project conventions, README guidance, and verbal agreements. It MUST be treated as the primary source of truth for all engineering decisions on the PoLocalCompare project.

**Amendment Procedure**:
- Amendments MUST be proposed via PR with a clear description of the change, the rationale, and the semantic version bump type (MAJOR / MINOR / PATCH).
- MAJOR bumps (principle removals, breaking redefinitions) require explicit sign-off from the project owner before merge.
- MINOR and PATCH bumps may be merged after peer review.
- The `LAST_AMENDED_DATE` and `CONSTITUTION_VERSION` MUST be updated in the same commit as the amendment.
- All dependent templates (plan, spec, tasks) MUST be reviewed for alignment in the same PR.

**Compliance**:
- All PRs and code reviews MUST verify compliance with this constitution.
- Any complexity that violates a principle MUST be justified in the plan's Complexity Tracking table.
- The `/LLMDOCS` folder is the authoritative runtime development guide for LLM-assisted development.

**Version**: 1.0.0 | **Ratified**: 2026-05-09 | **Last Amended**: 2026-05-09
