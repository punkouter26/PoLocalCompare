$ErrorActionPreference = 'Continue'

function Run-Once {
  param([string]$Label,[string]$Endpoint,[string]$CacheDir)
  if (Test-Path $CacheDir) { Remove-Item -Recurse -Force $CacheDir }
  New-Item -ItemType Directory -Path $CacheDir | Out-Null
  if ($Endpoint) { $env:HF_ENDPOINT = $Endpoint } else { Remove-Item Env:HF_ENDPOINT -ErrorAction SilentlyContinue }
  $env:HF_CACHE = $CacheDir
  Write-Output ('=== ' + $Label + ' endpoint=' + ($Endpoint ?? '') + ' ===')
  & python 'c:\Users\punko\Downloads\PoLocalCompare\SCRIPTS\_probe_one.py' 2>&1 | ForEach-Object { Write-Output $_ }
}

Run-Once -Label 'mirror'  -Endpoint 'https://hf-mirror.com' -CacheDir 'c:\Users\punko\Downloads\PoLocalCompare\.hf-mirror-test'
Run-Once -Label 'direct'  -Endpoint ''                     -CacheDir 'c:\Users\punko\Downloads\PoLocalCompare\.hf-direct-test'
Write-Output 'DONE'