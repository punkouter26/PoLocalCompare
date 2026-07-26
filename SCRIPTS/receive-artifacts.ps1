<#
.SYNOPSIS
  Installs the WebLLM model artifacts into wwwroot\models\ so the Blazor WASM client
  serves them as static files and browser models never touch huggingface.co.

.DESCRIPTION
  Accepts either layout:

    1. Split parts produced by .github\workflows\fetch-webllm-artifacts.yml
       (<model>.tar.part000, <model>.tar.part001, ..., <model>.sha256).
       This is the normal path:

           gh release download webllm-artifacts -D C:\hf-artifacts
           pwsh SCRIPTS\receive-artifacts.ps1 -PartsDir C:\hf-artifacts

    2. The single ~5 GB zip produced by SCRIPTS\fetch-artifacts.ps1 on an
       open-internet host:

           pwsh SCRIPTS\receive-artifacts.ps1 -ZipPath C:\hf-artifacts\polocalcompare-webllm-artifacts.zip

  With no arguments it looks in C:\hf-artifacts and picks whichever it finds.

  The expected model list is read from ModelSeeder.cs rather than hard-coded here, so
  a model added to the seeder is reported as missing instead of silently skipped.

.NOTES
  Weights land in wwwroot\models\<webLlmModelId>\ and the WebGPU libraries in
  wwwroot\models\_libs\. Both are gitignored — they are machine-local assets, not
  repo content. webllm-worker.js prefers _libs\ when present and falls back to
  raw.githubusercontent.com when it is not.
#>

param(
  [string]$PartsDir,
  [string]$ZipPath,
  [switch]$SkipHashCheck
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target   = Join-Path $repoRoot 'src\Client\PoLocalCompare.Client\wwwroot\models'
$seeder   = Join-Path $repoRoot 'src\PoLocalCompare.Api\Features\Models\ModelSeeder.cs'

if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
  throw 'tar is not on PATH. Windows 10 1803+ and Windows 11 ship bsdtar; install it or use -ZipPath.'
}

# Source of truth for which browser models this app expects.
$expected = @()
if (Test-Path $seeder) {
  $expected = [regex]::Matches((Get-Content -Raw $seeder), 'webLlmModelId:\s*"([^"]+)"') |
              ForEach-Object { $_.Groups[1].Value }
}
if (-not $expected) { Write-Warning "Could not read model ids from $seeder; skipping completeness check." }

if (-not $PartsDir -and -not $ZipPath) {
  $default = 'C:\hf-artifacts'
  $legacy  = Join-Path $default 'polocalcompare-webllm-artifacts.zip'
  if (Test-Path $default -PathType Container -ErrorAction SilentlyContinue) {
    if (Get-ChildItem -Path $default -Filter '*.tar.part*' -File -ErrorAction SilentlyContinue) { $PartsDir = $default }
    elseif (Test-Path $legacy) { $ZipPath = $legacy }
  }
  if (-not $PartsDir -and -not $ZipPath) {
    throw "Nothing to install. Expected split parts or a zip under $default. Run the 'Fetch WebLLM artifacts' workflow, then: gh release download webllm-artifacts -D $default"
  }
}

New-Item -ItemType Directory -Path $target -Force | Out-Null

function Install-FromParts {
  param([string]$Dir)

  $parts = Get-ChildItem -Path $Dir -File -Filter '*.tar.part*' |
           Group-Object { $_.Name -replace '\.tar\.part\d+$', '' }
  if (-not $parts) { throw "No *.tar.part* files found in $Dir" }

  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("polocalcompare-unpack-" + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $tmp | Out-Null
  try {
    foreach ($group in $parts) {
      $name  = $group.Name
      $chunks = $group.Group | Sort-Object Name
      Write-Output ("{0}: {1} part(s), {2:N0} MB" -f $name, $chunks.Count,
                    (($chunks | Measure-Object -Property Length -Sum).Sum / 1MB))

      $manifest = Join-Path $Dir "$name.sha256"
      if (-not $SkipHashCheck -and (Test-Path $manifest)) {
        # sha256sum format: "<hash>  <filename>". A truncated download is the single
        # most likely failure on a throttling proxy, so verify before spending time
        # reassembling gigabytes into a tar that will not extract.
        foreach ($line in Get-Content $manifest) {
          if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') { continue }
          $want = $Matches[1].ToLowerInvariant()
          $file = Join-Path $Dir $Matches[2].Trim()
          if (-not (Test-Path $file)) { throw "Missing part listed in $name.sha256: $($Matches[2])" }
          $got = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
          if ($got -ne $want) { throw "Checksum mismatch for $($Matches[2]) — re-download that part (curl -C - resumes)." }
        }
        Write-Output '  checksums OK'
      } elseif (-not $SkipHashCheck) {
        Write-Warning "  no $name.sha256 alongside the parts; skipping verification"
      }

      $tar = Join-Path $tmp "$name.tar"
      $out = [System.IO.File]::Create($tar)
      try {
        foreach ($chunk in $chunks) {
          $in = [System.IO.File]::OpenRead($chunk.FullName)
          try { $in.CopyTo($out) } finally { $in.Dispose() }
        }
      } finally { $out.Dispose() }

      # The archive root is wwwroot\models itself: it holds <modelId>\ plus _libs\<lib>.wasm,
      # so a stale model folder has to go before extracting or removed shards would linger.
      $dst = Join-Path $target $name
      if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }

      & tar -xf $tar -C $target
      if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $name (exit $LASTEXITCODE)" }
      Remove-Item -Force $tar

      Write-ModelSummary -Name $name
    }
  } finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
  }
}

function Install-FromZip {
  param([string]$Zip)

  if (-not (Test-Path $Zip)) { throw "Zip not found: $Zip" }
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("polocalcompare-unpack-" + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $tmp | Out-Null
  try {
    Write-Output ("Unpacking: " + $Zip)
    Expand-Archive -Path $Zip -DestinationPath $tmp -Force

    foreach ($src in Get-ChildItem -Path $tmp -Directory) {
      $dst = Join-Path $target $src.Name
      if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
      New-Item -ItemType Directory -Path $dst -Force | Out-Null
      robocopy $src.FullName $dst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
      # robocopy uses exit codes 0-7 for success; 8+ is a real failure.
      if ($LASTEXITCODE -ge 8) { throw "robocopy failed for $($src.Name) (exit $LASTEXITCODE)" }
      Write-ModelSummary -Name $src.Name
    }
  } finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
  }
}

function Write-ModelSummary {
  param([string]$Name)
  $dst   = Join-Path $target $Name
  $files = Get-ChildItem -Recurse -File $dst -ErrorAction SilentlyContinue
  $mb    = if ($files) { [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 0) } else { 0 }
  Write-Output ("  -> {0}: {1} files, {2} MB" -f $Name, ($files | Measure-Object).Count, $mb)
}

if ($PartsDir) { Install-FromParts -Dir $PartsDir } else { Install-FromZip -Zip $ZipPath }

Write-Output ''
$libs = Get-ChildItem -Path (Join-Path $target '_libs') -Filter '*.wasm' -File -ErrorAction SilentlyContinue
Write-Output ("WebGPU libraries in _libs\: {0}" -f ($libs | Measure-Object).Count)
if (-not $libs) {
  Write-Warning 'No .wasm libraries installed. Browser models will fall back to raw.githubusercontent.com, which this network may block.'
}

if ($expected) {
  $missing = $expected | Where-Object {
    $dir = Join-Path $target $_
    -not (Test-Path (Join-Path $dir 'mlc-chat-config.json'))
  }
  if ($missing) {
    Write-Output ''
    Write-Warning ("Still missing ({0} of {1}): {2}" -f $missing.Count, $expected.Count, ($missing -join ', '))
    Write-Output 'Re-run the workflow for those ids: Actions -> Fetch WebLLM artifacts -> models: <comma-separated list>'
  } else {
    Write-Output ''
    Write-Output ("All {0} browser models are installed." -f $expected.Count)
    Write-Output 'Next: start the app and run the duels — the Lab should now report source=local for every Local model.'
  }
}
