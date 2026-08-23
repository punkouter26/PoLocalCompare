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
- Stores weights at `src/PoLocalCompare.Client/wwwroot/models/{webLlmModelId}/` and
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
- ~5 GB disk space, and egress to huggingface.co + raw.githubusercontent.com.
  Both hosts are required: weights come from huggingface.co and the WebGPU `model_lib`
  `.wasm` files from raw.githubusercontent.com. Vendoring only one half is the failure
  mode to watch for — `webllm-worker.js` prefers local and falls back to the CDN per file,
  so a half-populated `wwwroot/models/` fails silently on the missing half.

---

### `plan-webllm-artifacts.py`

**Purpose:** Single source of the browser-model list. It derives id, HuggingFace repo, and
`.wasm` library filename by parsing `ModelSeeder.cs` and `web-llm.js` at run time, and exits
non-zero when the two disagree.

**When to run:** After any edit to the model catalog — it is the catalog-drift check.

```bash
python SCRIPTS/plan-webllm-artifacts.py
```

`download-models.py` calls it rather than carrying its own list, so seeding a new browser model
in `ModelSeeder.cs` is the only edit needed. (An earlier hand-maintained list had already lost
`SmolLM2-135M` and would have mismapped `Qwen2.5-0.5B`, whose library is named `Qwen2-0.5B-...`.)

Note it parses `web-llm.js`, which is a **Git LFS object**. A clone made without `git lfs install`
has a ~130-byte pointer file there and this script will not find any models.

---

### `test-browser-models.ps1` / `test-browser-models.cjs`

**Purpose:** Drives a headless browser against a running app to prove WebGPU inference actually
works for each seeded browser model. Not part of CI — no runner has a GPU.

```powershell
pwsh SCRIPTS/test-browser-models.ps1   # app must already be running on https://localhost:5001
```

---

## Prerequisites

Both Python scripts use only the standard library — there is nothing to `pip install`. They need
Python 3.12+, and `download-models.py` additionally needs `curl.exe` (shipped with Windows 10
1803+). The `huggingface_hub` dependency here was only ever needed by `fetch-artifacts.ps1`, which
was removed on 2026-08-23.

Run all scripts from the repository root:
```bash
cd /path/to/PoLocalCompare
python SCRIPTS/script-name.py
```
