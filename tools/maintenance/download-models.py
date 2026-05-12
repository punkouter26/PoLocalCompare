"""
Download WebLLM MLC model files from HuggingFace to wwwroot/models/.
Run from repo root: python tools/maintenance/download-models.py

Models are stored at src/Client/PoLocalCompare.Client/wwwroot/models/{modelId}/
and served as static files at https://localhost:5001/models/{modelId}/
"""

import sys
from pathlib import Path
from huggingface_hub import snapshot_download

MODELS = [
    "mlc-ai/Qwen2.5-0.5B-Instruct-q4f32_1-MLC",
    "mlc-ai/Llama-3.2-1B-Instruct-q4f16_1-MLC",
    "mlc-ai/Phi-3.5-mini-instruct-q4f32_1-MLC",
    "mlc-ai/Llama-3.2-3B-Instruct-q4f16_1-MLC",
    "mlc-ai/Llama-3.1-8B-Instruct-q4f16_1-MLC",
    "mlc-ai/gemma-2-2b-it-q4f16_1-MLC",
    "mlc-ai/Phi-4-mini-instruct-q4f16_1-MLC",
    "mlc-ai/Mistral-7B-Instruct-v0.3-q4f16_1-MLC",
    "mlc-ai/Qwen2.5-7B-Instruct-q4f16_1-MLC",
    "mlc-ai/SmolLM2-135M-Instruct-q0f32-MLC",
    "mlc-ai/SmolLM2-360M-Instruct-q4f32_1-MLC",
    "mlc-ai/SmolLM2-1.7B-Instruct-q4f16_1-MLC",
    "mlc-ai/Qwen3-1.7B-q4f16_1-MLC",
]

IGNORE = [
    ".gitattributes",
    "README.md",
    "ndarray-cache-b16.json",
]

BASE_DIR = Path(__file__).resolve().parents[2] / "src" / "Client" / "PoLocalCompare.Client" / "wwwroot" / "models"


def download(repo_id: str) -> None:
    model_id = repo_id.split("/")[1]
    local_dir = BASE_DIR / model_id
    local_dir.mkdir(parents=True, exist_ok=True)
    print(f"\n{'=' * 60}")
    print(f"  Downloading: {model_id}")
    print(f"  Destination: {local_dir}")
    print(f"{'=' * 60}")
    snapshot_download(
        repo_id=repo_id,
        local_dir=str(local_dir),
        repo_type="model",
        ignore_patterns=IGNORE,
    )
    size_mb = sum(f.stat().st_size for f in local_dir.rglob("*") if f.is_file()) / (1024 * 1024)
    print(f"  Done - {size_mb:.0f} MB on disk")


def main() -> None:
    import argparse
    parser = argparse.ArgumentParser(description="Download WebLLM MLC model files from HuggingFace.")
    parser.add_argument(
        "--model",
        metavar="MODEL_ID",
        help="Download only this model ID (e.g. SmolLM2-135M-Instruct-q0f32-MLC). Omit to download all.",
    )
    args = parser.parse_args()

    if args.model:
        models = [r for r in MODELS if r.split("/")[1] == args.model]
        if not models:
            print(f"ERROR: '{args.model}' not found in MODELS list.", file=sys.stderr)
            print("Available IDs:", ", ".join(r.split("/")[1] for r in MODELS))
            sys.exit(1)
    else:
        models = MODELS

    print(f"Model output directory: {BASE_DIR}")
    for repo_id in models:
        try:
            download(repo_id)
        except Exception as exc:
            print(f"ERROR downloading {repo_id}: {exc}", file=sys.stderr)
            sys.exit(1)
    print(f"\nDone. Downloaded {len(models)} model(s) to: {BASE_DIR}")


if __name__ == "__main__":
    main()