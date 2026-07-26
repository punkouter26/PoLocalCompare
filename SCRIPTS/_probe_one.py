import os, sys, traceback
from huggingface_hub import hf_hub_download
endpoint = os.environ.get('HF_ENDPOINT', '(default huggingface.co)')
cache_dir = os.environ.get('HF_CACHE', r'c:\Users\punko\Downloads\PoLocalCompare\.hf-probe')
os.makedirs(cache_dir, exist_ok=True)
try:
    p = hf_hub_download(
        repo_id='mlc-ai/SmolLM2-360M-Instruct-q4f32_1-MLC',
        filename='mlc-chat-config.json',
        repo_type='model',
        cache_dir=cache_dir,
    )
    print(f'OK endpoint={endpoint} path={p}')
except Exception as e:
    print(f'FAIL endpoint={endpoint}')
    traceback.print_exc()
    sys.exit(1)
