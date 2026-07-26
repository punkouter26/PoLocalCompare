<#
.SYNOPSIS
  Downloads every WebLLM browser-model artifact (weights + WebGPU library) into a
  self-contained zip, ready to drop into the PoLocalCompare repo.

.DESCRIPTION
  IMPORTANT: this script needs OPEN INTERNET to huggingface.co. If your machine is
  behind a corporate proxy / firewall that MITMs TLS, you will see
  "WinError 10054: an existing connection was forcibly closed by the remote host"
  the moment huggingface_hub tries to stream any model file.

  If you do not have an open-internet host to run this on, use the CI path instead —
  it needs nothing but a browser:

      Actions -> "Fetch WebLLM artifacts" -> Run workflow
      gh release download webllm-artifacts -D C:\hf-artifacts
      pwsh SCRIPTS\receive-artifacts.ps1 -PartsDir C:\hf-artifacts

  Output of this script:
      C:\hf-artifacts\polocalcompare-webllm-artifacts.zip (~5 GB)
  containing one folder per model plus _libs\ with the WebGPU .wasm libraries.

  Once the zip lands on the target machine:
      pwsh SCRIPTS\receive-artifacts.ps1 -ZipPath C:\hf-artifacts\polocalcompare-webllm-artifacts.zip

.NOTES
  The model list is not hard-coded here. SCRIPTS\plan-webllm-artifacts.py derives it
  from ModelSeeder.cs and web-llm.js, which is also what the CI workflow uses — so
  the two paths can never drift apart or miss a newly seeded model.

  Honors HF_ENDPOINT / HF_TOKEN / HF_HUB_DOWNLOAD_TIMEOUT in the environment.
#>

param(
  [string]$OutputRoot = 'C:\hf-artifacts',
  [string]$Models = 'all'
)

$ErrorActionPreference = 'Stop'

$planner = Join-Path $PSScriptRoot 'plan-webllm-artifacts.py'

Write-Output '[1/4] Verifying Python + huggingface_hub'
if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
  Write-Error 'python not on PATH. Install Python 3.10+ and run: pip install huggingface_hub'
  exit 2
}
& python -c "import huggingface_hub; print('huggingface_hub', huggingface_hub.__version__)"
if ($LASTEXITCODE -ne 0) {
  Write-Error 'huggingface_hub missing. Run: pip install huggingface_hub'
  exit 2
}

Write-Output '[2/4] Resolving the model list from ModelSeeder.cs + web-llm.js'
$env:MODELS = $Models
$plan = & python $planner
if ($LASTEXITCODE -ne 0) { Write-Error 'plan-webllm-artifacts.py failed.'; exit 2 }

$matrixLine  = $plan | Where-Object { $_ -like 'matrix=*' }   | Select-Object -First 1
$libBaseLine = $plan | Where-Object { $_ -like 'lib_base=*' } | Select-Object -First 1
if (-not $matrixLine -or -not $libBaseLine) { Write-Error 'planner produced no matrix/lib_base.'; exit 2 }

$models  = ($matrixLine  -replace '^matrix=','')   | ConvertFrom-Json
$libBase = ($libBaseLine -replace '^lib_base=','')
Write-Output ("  {0} model(s); libs from {1}" -f $models.Count, $libBase)

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputRoot '_libs') -Force | Out-Null

Write-Output '[3/4] Probing reachability of huggingface.co'
$probe = & python -c @"
import sys
from huggingface_hub import hf_hub_download
try:
    p = hf_hub_download(repo_id='$($models[0].repo)', filename='mlc-chat-config.json', repo_type='model')
    print('OK ' + p)
except Exception as e:
    msg = type(e).__name__ + ': ' + str(e)
    if 'WinError 10054' in msg or 'forcibly closed' in msg or '401' in msg or 'Unauthorized' in msg:
        print('NETBLOCK')
    else:
        print('ERR ' + msg)
    sys.exit(2)
"@
if ($LASTEXITCODE -ne 0 -or $probe -like 'NETBLOCK*') {
  Write-Host ''
  Write-Host '===========================================================' -ForegroundColor Yellow
  Write-Host ' Could not reach huggingface.co from this machine.' -ForegroundColor Yellow
  Write-Host ' This is almost certainly a corporate proxy / firewall issue.' -ForegroundColor Yellow
  Write-Host '' -ForegroundColor Yellow
  Write-Host ' Options:' -ForegroundColor Yellow
  Write-Host '   1. Use CI instead - no open-internet host needed:' -ForegroundColor Yellow
  Write-Host '        Actions -> "Fetch WebLLM artifacts" -> Run workflow' -ForegroundColor Yellow
  Write-Host '        gh release download webllm-artifacts -D C:\hf-artifacts' -ForegroundColor Yellow
  Write-Host '        pwsh SCRIPTS\receive-artifacts.ps1 -PartsDir C:\hf-artifacts' -ForegroundColor Yellow
  Write-Host '   2. Run this script on a dev VM or home laptop.' -ForegroundColor Yellow
  Write-Host '   3. Set HF_ENDPOINT=https://hf-mirror.com if your proxy allows it.' -ForegroundColor Yellow
  Write-Host '===========================================================' -ForegroundColor Yellow
  exit 3
}
Write-Output ('  ' + $probe)

Write-Output '[4/4] Snapshotting weights and fetching WebGPU libraries'
foreach ($m in $models) {
  $dest = Join-Path $OutputRoot $m.dir
  New-Item -ItemType Directory -Path $dest -Force | Out-Null
  Write-Output ("- " + $m.repo)
  & python -c "from huggingface_hub import snapshot_download; snapshot_download(repo_id='$($m.repo)', local_dir=r'$dest', repo_type='model', ignore_patterns=['.gitattributes','README.md','ndarray-cache-b16.json'], max_workers=4)"
  if ($LASTEXITCODE -ne 0) {
    Write-Error "snapshot_download failed for $($m.repo). If that was another 'WinError 10054' or '401 Unauthorized', this host cannot reach HuggingFace — use the CI path described above."
    exit 4
  }

  # The .wasm library is fetched separately: WebLLM loads it from
  # raw.githubusercontent.com, a host the target network may block independently of
  # huggingface.co. Shipping it in the zip means the client needs no third-party egress.
  $libDest = Join-Path (Join-Path $OutputRoot '_libs') $m.lib
  Write-Output ("  lib: " + $m.lib)
  Invoke-WebRequest -Uri ("{0}/{1}" -f $libBase, $m.lib) -OutFile $libDest -UseBasicParsing
}

$zip = Join-Path $OutputRoot 'polocalcompare-webllm-artifacts.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Output ('Compressing to: ' + $zip)
$paths = @(Join-Path $OutputRoot '_libs') + ($models | ForEach-Object { Join-Path $OutputRoot $_.dir })
Compress-Archive -Path $paths -DestinationPath $zip -CompressionLevel Optimal

$szMB = [math]::Round((Get-Item $zip).Length / 1MB, 0)
Write-Output ("DONE | size=" + $szMB + " MB | " + $zip)
Write-Output "Now: copy this zip to the target machine and run pwsh SCRIPTS\receive-artifacts.ps1 -ZipPath <path>"
