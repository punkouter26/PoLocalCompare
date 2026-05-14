# Scripts

Utility scripts for maintaining and managing the PoLocalCompare application.

## Available Scripts

### `download-models.py`

**Purpose:** Downloads WebLLM MLC model files from HuggingFace.

**Usage:**
```bash
python SCRIPTS/download-models.py
```

**What it does:**
- Downloads a curated list of MLC-optimized language models from HuggingFace Hub
- Stores models at `src/Client/PoLocalCompare.Client/wwwroot/models/{modelId}/`
- Models are served as static files at `https://localhost:5001/models/{modelId}/`
- Skips unnecessary files (`.gitattributes`, `README.md`, `ndarray-cache-b16.json`)
- Reports download progress and disk space used for each model

**Models downloaded:**
- Qwen2.5-0.5B, 7B variants
- Llama-3.2 1B, 3B; Llama-3.1 8B
- Phi-3.5-mini, Phi-4-mini
- Mistral-7B-Instruct-v0.3
- gemma-2-2b-it
- SmolLM2 (135M, 360M, 1.7B variants)
- Qwen3-1.7B

**Requirements:**
- `huggingface_hub` Python package
- ~20+ GB disk space

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
