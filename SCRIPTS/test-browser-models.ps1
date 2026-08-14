<#
.SYNOPSIS
Smoke-tests every browser (WebLLM/WebGPU) model against a running app.

.DESCRIPTION
Local models execute in the client, not on the server, so the API alone cannot test them --
a server-side probe can only mark them NOT_TESTED_BROWSER_REQUIRED. This starts a
real Chrome, attaches Playwright to it over CDP, and runs one duel per browser model.

Why a real Chrome instead of Playwright's own: a Playwright-launched browser (bundled Chromium
*or* channel:'chrome') only exposes the SwiftShader fallback adapter here -- isFallbackAdapter
true and no shader-f16. Every q4f16 model would fail identically for a reason that has nothing
to do with the model. Chrome started normally gets the real GPU, and --force-high-performance-gpu
picks the discrete one over integrated graphics.

.PARAMETER Only
Comma-separated display names or model ids, to test a subset.

.PARAMETER TimeoutMs
Per-model ceiling. The default is generous because the first run of a model compiles WebGPU
shaders on top of loading the weights.

.EXAMPLE
pwsh SCRIPTS/test-browser-models.ps1
pwsh SCRIPTS/test-browser-models.ps1 -Only 'SmolLM2 135M,SmolLM2 360M'
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'https://localhost:5001',
    [string] $Only = '',
    [int]    $TimeoutMs = 900000,
    [int]    $Port = 9222,
    [string] $Out = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$driver = Join-Path $repoRoot 'tests\PoLocalCompare.E2EUI\bin\Debug\net10.0\.playwright'
$node = Join-Path $driver 'node\win32_x64\node.exe'
$package = Join-Path $driver 'package'
if (-not (Test-Path $node) -or -not (Test-Path $package)) {
    throw "Playwright driver not found at $driver. Run: dotnet build tests/PoLocalCompare.E2EUI"
}

$chrome = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw 'Google Chrome not found. WebGPU needs it; Playwright''s Chromium only offers SwiftShader.' }

try { Invoke-RestMethod "$BaseUrl/health" -SkipCertificateCheck -TimeoutSec 5 | Out-Null }
catch { throw "App is not responding at $BaseUrl. Start it first: dotnet run --project src/PoLocalCompare.Api --launch-profile https" }

# Chrome is started and stopped per model by the Node script, not once here: closing a page does
# not release a model's WebGPU context, and running several models in one Chrome exhausted the
# GPU so that the fourth died with "GPU device was lost while loading the model". Each model
# therefore gets its own process and its own throwaway profile.
$env:PW_PACKAGE = $package
$env:CHROME_PATH = $chrome
$env:BASE_URL = $BaseUrl
$env:CDP_PORT = "$Port"
$env:TIMEOUT_MS = "$TimeoutMs"
$env:ONLY = $Only
if ($Out) { $env:OUT = $Out }
# Node's fetch has its own trust store and rejects the ASP.NET dev certificate. Scoped to this
# child process, which only ever talks to the local app.
if ($BaseUrl -match '^https://(localhost|127\.0\.0\.1)') { $env:NODE_TLS_REJECT_UNAUTHORIZED = '0' }

Write-Host "Chrome: $chrome (one instance per model, discrete GPU)" -ForegroundColor Cyan

& $node (Join-Path $PSScriptRoot 'test-browser-models.cjs')
exit $LASTEXITCODE
