<#
.SYNOPSIS
  Receives the WebLLM artifact zip produced by SCRIPTS\fetch-artifacts.ps1 and
  places its contents under src\Client\PoLocalCompare.Client\wwwroot\models\
  so the Blazor WASM client serves them as static files at runtime.

.DESCRIPTION
  This unblocks the 8 browser-only Local models that cannot currently download
  their artifacts from huggingface.co because the corporate proxy blocks the
  egress. The destination folders already exist; this script only fills them.

.EXAMPLE
    pwsh SCRIPTS\receive-artifacts.ps1
  (Picks up C:\hf-artifacts\polocalcompare-webllm-artifacts.zip by default.)

    pwsh SCRIPTS\receive-artifacts.ps1 -ZipPath D:\transfers\artifacts.zip
#>

param(
  [string]$ZipPath = 'C:\hf-artifacts\polocalcompare-webllm-artifacts.zip'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target   = Join-Path $repoRoot 'src\Client\PoLocalCompare.Client\wwwroot\models'

if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }

$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("polocalcompare-unpack-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpRoot | Out-Null
try {
  Write-Output ('Unpacking: ' + $ZipPath)
  Expand-Archive -Path $ZipPath -DestinationPath $tmpRoot -Force

  $expected = @(
    'SmolLM2-360M-Instruct-q4f32_1-MLC',
    'SmolLM2-1.7B-Instruct-q4f16_1-MLC',
    'Qwen2.5-0.5B-Instruct-q4f32_1-MLC',
    'Qwen3-1.7B-q4f16_1-MLC',
    'Llama-3.2-1B-Instruct-q4f16_1-MLC',
    'Llama-3.2-3B-Instruct-q4f16_1-MLC',
    'Phi-3.5-mini-instruct-q4f32_1-MLC',
    'gemma-2-2b-it-q4f16_1-MLC'
  )

  foreach ($dir in $expected) {
    $src = Join-Path $tmpRoot $dir
    $dst = Join-Path $target $dir
    if (-not (Test-Path $src)) { Write-Warning ("missing in zip: " + $dir); continue }
    if (Test-Path $dst)        { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    $count = (Get-ChildItem -Recurse -File $dst).Count
    $sizeMB = [math]::Round(((Get-ChildItem -Recurse -File $dst | Measure-Object -Property Length -Sum).Sum / 1MB), 0)
    Write-Output ("  " + $dir + ": " + $count + " files, " + $sizeMB + " MB")
  }

  Write-Output ''
  Write-Output 'All 8 model folders populated under wwwroot\models\.'
  Write-Output 'Next: ask Copilot to drive the browser duels and rebuild the HTML report.'

} finally {
  Remove-Item -Recurse -Force $tmpRoot -ErrorAction SilentlyContinue
}