<#
.SYNOPSIS
  Downloads the 8 outstanding WebLLM model artifacts from HuggingFace into a
  self-contained zip, ready to drop into the PoLocalCompare repo.

.DESCRIPTION
  Run this on a Windows machine that has open internet access (e.g. your dev VM,
  a colleague's laptop, a CI runner). It uses huggingface_hub.snapshot_download
  via the system Python; it does NOT need any HF token. Output is a zip at
  C:\hf-artifacts\polocalcompare-webllm-artifacts.zip (~5 GB).

  Once the zip is on this machine, run
      pwsh SCRIPTS\receive-artifacts.ps1
  to expand it into src\Client\PoLocalCompare.Client\wwwroot\models\.

.NOTES
  - Tested on Python 3.10+. huggingface_hub>=1.22 recommended.
  - If `HF_ENDPOINT` is set in the environment, huggingface_hub will use it (e.g.
    to bypass corporate proxies that block huggingface.co).
#>

$ErrorActionPreference = 'Stop'

$models = @(
  @{ repo='mlc-ai/SmolLM2-360M-Instruct-q4f32_1-MLC' ; dir='SmolLM2-360M-Instruct-q4f32_1-MLC' }
  @{ repo='mlc-ai/SmolLM2-1.7B-Instruct-q4f16_1-MLC' ; dir='SmolLM2-1.7B-Instruct-q4f16_1-MLC' }
  @{ repo='mlc-ai/Qwen2.5-0.5B-Instruct-q4f32_1-MLC'; dir='Qwen2.5-0.5B-Instruct-q4f32_1-MLC' }
  @{ repo='mlc-ai/Qwen3-1.7B-q4f16_1-MLC'           ; dir='Qwen3-1.7B-q4f16_1-MLC' }
  @{ repo='mlc-ai/Llama-3.2-1B-Instruct-q4f16_1-MLC' ; dir='Llama-3.2-1B-Instruct-q4f16_1-MLC' }
  @{ repo='mlc-ai/Llama-3.2-3B-Instruct-q4f16_1-MLC' ; dir='Llama-3.2-3B-Instruct-q4f16_1-MLC' }
  @{ repo='mlc-ai/Phi-3.5-mini-instruct-q4f32_1-MLC' ; dir='Phi-3.5-mini-instruct-q4f32_1-MLC' }
  @{ repo='mlc-ai/gemma-2-2b-it-q4f16_1-MLC'        ; dir='gemma-2-2b-it-q4f16_1-MLC' }
)

$root = 'C:\hf-artifacts'
New-Item -ItemType Directory -Path $root -Force | Out-Null

Write-Output '[1/2] Verifying Python + huggingface_hub'
$py = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $py) { throw 'python not on PATH. Run: pip install huggingface_hub' }
& python -c "import huggingface_hub; print('huggingface_hub', huggingface_hub.__version__)"
if ($LASTEXITCODE -ne 0) { throw 'huggingface_hub missing. Run: pip install huggingface_hub' }

Write-Output '[2/2] Snapshotting 8 repos via huggingface_hub.snapshot_download'

foreach ($m in $models) {
  $dest = Join-Path $root $m.dir
  New-Item -ItemType Directory -Path $dest -Force | Out-Null
  Write-Output ("- " + $m.repo)
  & python -c "from huggingface_hub import snapshot_download; snapshot_download(repo_id='$($m.repo)', local_dir=r'$dest', repo_type='model', ignore_patterns=['.gitattributes','README.md','ndarray-cache-b16.json'], max_workers=4)"
  if ($LASTEXITCODE -ne 0) { throw "snapshot_download failed for $($m.repo)" }
}

$zip = Join-Path $root 'polocalcompare-webllm-artifacts.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Output ('Compressing to: ' + $zip)
Compress-Archive -Path "$root\SmolLM2-360M-Instruct-q4f32_1-MLC", "$root\SmolLM2-1.7B-Instruct-q4f16_1-MLC", "$root\Qwen2.5-0.5B-Instruct-q4f32_1-MLC", "$root\Qwen3-1.7B-q4f16_1-MLC", "$root\Llama-3.2-1B-Instruct-q4f16_1-MLC", "$root\Llama-3.2-3B-Instruct-q4f16_1-MLC", "$root\Phi-3.5-mini-instruct-q4f32_1-MLC", "$root\gemma-2-2b-it-q4f16_1-MLC" -DestinationPath $zip -CompressionLevel Optimal

$szMB = [math]::Round((Get-Item $zip).Length / 1MB, 0)
Write-Output ("DONE | size=" + $szMB + " MB | " + $zip)
Write-Output "Now: copy this zip to your sandbox machine and run pwsh SCRIPTS\receive-artifacts.ps1"