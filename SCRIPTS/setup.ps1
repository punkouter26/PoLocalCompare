#Requires -Version 7
<#
.SYNOPSIS
    One-command setup for PoLocalCompare on a new Windows machine.

.DESCRIPTION
    Installs prerequisites via Winget, starts Docker/Azurite, and configures
    local mock keys so the app runs first-time without manual steps.

    Run from the repo root:
        pwsh SCRIPTS/setup.ps1

.NOTES
    Standards §9 & §10 — One-Command Setup & Portability.
#>

[CmdletBinding()]
param (
    [switch]$SkipWinget,
    [switch]$SkipDocker,
    [switch]$SkipModels
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$Msg) { Write-Host "`n==> $Msg" -ForegroundColor Cyan }
function Write-Ok([string]$Msg)   { Write-Host "    [OK] $Msg" -ForegroundColor Green }
function Write-Warn([string]$Msg) { Write-Host "    [WARN] $Msg" -ForegroundColor Yellow }

# ─── 1. Prerequisites via Winget ─────────────────────────────────────────────
if (-not $SkipWinget) {
    Write-Step "Installing prerequisites via Winget"

    $packages = @(
        @{ Id = 'Microsoft.DotNet.SDK.10';         Name = '.NET 10 SDK' },
        @{ Id = 'Docker.DockerDesktop';             Name = 'Docker Desktop' },
        @{ Id = 'Python.Python.3.12';               Name = 'Python 3.12' },
        @{ Id = 'Microsoft.AzureCLI';               Name = 'Azure CLI' }
    )

    foreach ($pkg in $packages) {
        $installed = winget list --id $pkg.Id --exact --accept-source-agreements 2>$null |
                     Select-String $pkg.Id
        if ($installed) {
            Write-Ok "$($pkg.Name) already installed"
        } else {
            Write-Host "    Installing $($pkg.Name)..." -ForegroundColor DarkCyan
            winget install --id $pkg.Id --exact --silent --accept-package-agreements --accept-source-agreements
            Write-Ok "$($pkg.Name) installed"
        }
    }
}

# ─── 2. Docker + Azurite ──────────────────────────────────────────────────────
if (-not $SkipDocker) {
    Write-Step "Starting Azurite (Azure Storage emulator) via Docker"

    $dockerRunning = docker info 2>$null
    if (-not $dockerRunning) {
        Write-Warn "Docker Desktop is not running. Please start it and re-run this script."
        exit 1
    }

    $azuriteRunning = docker ps --filter "name=azurite" --format "{{.Names}}" 2>$null
    if ($azuriteRunning -match 'azurite') {
        Write-Ok "Azurite container already running"
    } else {
        $azuriteExists = docker ps -a --filter "name=azurite" --format "{{.Names}}" 2>$null
        if ($azuriteExists -match 'azurite') {
            docker start azurite | Out-Null
            Write-Ok "Azurite container started"
        } else {
            docker run -d --name azurite `
                -p 10000:10000 -p 10001:10001 -p 10002:10002 `
                mcr.microsoft.com/azure-storage/azurite | Out-Null
            Write-Ok "Azurite container created and started"
        }
    }
}

# ─── 3. Local mock / dev keys ─────────────────────────────────────────────────
Write-Step "Configuring local dev settings"

$devSettings = Join-Path $RepoRoot 'src\PoLocalCompare.Api\appsettings.Development.json'
if (-not (Test-Path $devSettings)) {
    $template = @{
        Logging = @{
            LogLevel = @{
                Default             = 'Debug'
                'Microsoft.AspNetCore' = 'Information'
            }
        }
        KeyVault = @{
            Uri = 'https://kv-poshared.vault.azure.net/'
        }
        ApplicationInsights = @{
            ConnectionString = ''
        }
        ConnectionStrings = @{
            AzureTableStorage = 'UseDevelopmentStorage=true'
            AzureBlobStorage  = 'UseDevelopmentStorage=true'
        }
        Features = @{
            UseRealAi = $false
        }
    } | ConvertTo-Json -Depth 5
    $template | Set-Content $devSettings -Encoding UTF8
    Write-Ok "Created appsettings.Development.json with Azurite defaults"
} else {
    Write-Ok "appsettings.Development.json already exists"
}

# ─── 4. Restore .NET dependencies ────────────────────────────────────────────
Write-Step "Restoring .NET packages"
Push-Location $RepoRoot
try {
    dotnet restore PoLocalCompare.slnx --verbosity quiet
    Write-Ok "Packages restored"
} finally {
    Pop-Location
}

# ─── 5. Download local LLM models (optional) ─────────────────────────────────
if (-not $SkipModels) {
    Write-Step "Downloading WebLLM models (this may take a while; skip with -SkipModels)"
    $downloadScript = Join-Path $PSScriptRoot 'download-models.py'
    if (Test-Path $downloadScript) {
        python $downloadScript
        Write-Ok "Models downloaded"
    } else {
        Write-Warn "download-models.py not found — skipping model download"
    }
}

# ─── Done ─────────────────────────────────────────────────────────────────────
Write-Host "`n==> Setup complete!" -ForegroundColor Green
Write-Host "    Run the app:  dotnet watch run --project src/PoLocalCompare.Api/PoLocalCompare.Api.csproj --launch-profile https" -ForegroundColor White
Write-Host "    Open:         https://localhost:5001" -ForegroundColor White
