# Scripts

Utility scripts for maintaining and managing the PoLocalCompare application.

## Available Scripts

### `setup.ps1` ⭐ Start here

**Purpose:** One-command setup for a new Windows development machine (standards §9 & §10).

**When to run:** First checkout on any new machine.

**Usage:**
```powershell
pwsh SCRIPTS/setup.ps1

# Flags:
#   -SkipWinget   skip Winget installs (tools already present)
#   -SkipDocker   skip Docker/Azurite startup
#   -SkipModels   skip WebLLM model download
```

**What it does:**
1. Installs .NET 10 SDK, Docker Desktop, Python 3.12, Azure CLI via Winget
2. Starts Azurite in Docker (creates container if needed)
3. Creates `appsettings.Development.json` with Azurite defaults if missing
4. Restores .NET NuGet packages
5. Downloads WebLLM models via `download-models.py`

---

### `download-models.py`

**Purpose:** Downloads WebLLM MLC model weights and WebGPU model libraries from HuggingFace.

**When to run:** First-time setup on any new development PC before using Local Model Lab.

**Usage:**
```bash
python SCRIPTS/download-models.py                        # every seeded model (~5 GB)
MODELS=small python SCRIPTS/download-models.py           # 3 smallest, ~1 GB
MODELS=Qwen3-1.7B-q4f16_1-MLC python SCRIPTS/download-models.py
```

**What it does:**
- Asks `plan-webllm-artifacts.py` which models are in scope, so the list comes from
  `ModelSeeder.cs` — seeding a new browser model needs no edit here
- Stores weights at `src/Client/PoLocalCompare.Client/wwwroot/models/{webLlmModelId}/` and
  libraries at `.../models/_libs/`, served from `https://localhost:5001/models/`
- Skips unnecessary files (`.gitattributes`, `README.md`, `ndarray-cache-b16.json`)
- Verifies every LFS file against the sha256 the Hub advertises, and re-fetches on mismatch
- Re-running is cheap: files already on disk with the right hash are skipped

**Why it shells out to `curl.exe` rather than using `huggingface_hub`:** on some networks TLS
handshakes to huggingface.co are reset at random — Windows schannel cannot reach the certificate
revocation responder (`CRYPT_E_REVOCATION_OFFLINE`), and roughly half of new connections get reset
regardless. Python's TLS stack fails the same way and `huggingface_hub` gives up on the first
error. `curl` with `--retry`/`--ssl-no-revoke` and byte-range resume rides through it; established
connections run at full speed, and the sha256 check means the loosened revocation check costs no
integrity.

**Requirements:**
- `curl.exe` — shipped with Windows 10 1803+
- ~5 GB disk space, and egress to huggingface.co + raw.githubusercontent.com
  (if you have neither, use the GitHub Actions route below)

---

### `cleanup-models.py`

**Purpose:** Deduplicates the Models table in Azurite (Azure Storage emulator).

**Usage:**
```bash
python SCRIPTS/cleanup-models.py
```

**What it does:**
- Connects to the local Azurite development storage
- Groups model entries by `WebLlmModelId`
- Keeps the oldest entry (by insertion order/ULID) for each model
- Deletes all duplicate entries
- Reports the number of duplicates removed and remaining models

**Requirements:**
- Azurite running with a `Models` table
- `azure-data-tables` Python package
- Connection string set to `UseDevelopmentStorage=true`

**When to use:**
- After testing model ingestion workflows
- To clean up development data and maintain a clean state
- Before running integration tests

---

### `plan-webllm-artifacts.py`, `fetch-artifacts.ps1`, `receive-artifacts.ps1`

**Purpose:** Get browser (WebLLM) models onto a machine whose network blocks huggingface.co.
`download-models.py` above needs open egress; these three do not.

Try `download-models.py` first — it survives a flaky connection, and only a hard block needs this
route. When you do need it, it takes nothing but a browser and the repo:

```powershell
# 1. GitHub -> Actions -> "Fetch WebLLM artifacts" -> Run workflow
#    models: all | small | <comma-separated webLlmModelIds>
#    Start with `small` (3 models, ~1 GB) to prove the pipeline before pulling ~5 GB.

# 2. Download what it published
gh release download webllm-artifacts -D C:\hf-artifacts

# 3. Install into wwwroot\models\
pwsh SCRIPTS\receive-artifacts.ps1 -PartsDir C:\hf-artifacts
```

The workflow publishes **release assets**, not Actions artifacts: artifacts count against the
repo's storage quota and download as a single unresumable zip, whereas each `*.tar.partNNN` asset
has its own URL that `curl -C -` can resume — which matters on a throttling proxy. Each part is
checksummed in `<model>.sha256` and verified before extraction.

**What lands where:**
- `wwwroot/models/<webLlmModelId>/` — the MLC weights
- `wwwroot/models/_libs/<name>.wasm` — the WebGPU model libraries

The `_libs` half is not optional. WebLLM's `prebuiltAppConfig` resolves `model_lib` against
raw.githubusercontent.com, a *different host* from huggingface.co that a proxy can block on its
own, so populating the weights alone still leaves a ~5 MB per-model fetch to the open internet.
`webllm-worker.js` prefers `_libs/` when it resolves and falls back to the CDN when it does not.

**`fetch-artifacts.ps1`** is the alternative for when you *do* have an open-internet host: it
produces one ~5 GB zip you ferry across by hand, which `receive-artifacts.ps1 -ZipPath` also
accepts.

**`plan-webllm-artifacts.py`** is not run directly. It derives the model list — id, HuggingFace
repo, and `.wasm` filename — from `ModelSeeder.cs` and `web-llm.js` at run time. Both the workflow
and `fetch-artifacts.ps1` call it, so seeding a new browser model in `ModelSeeder.cs` is the only
edit needed and the two paths cannot drift. (An earlier hand-maintained list had already lost
`SmolLM2-135M` and would have mismapped `Qwen2.5-0.5B`, whose library is named `Qwen2-0.5B-...`.)

**Requirements:** `gh` CLI (or download the assets from the Releases page), and `tar` — shipped
with Windows 10 1803+. `fetch-artifacts.ps1` additionally needs Python + `huggingface_hub`.

---

## Prerequisites

Install required Python packages:
```bash
pip install huggingface_hub azure-data-tables
```

Run all scripts from the repository root:
```bash
cd /path/to/PoLocalCompare
python SCRIPTS/script-name.py
```
