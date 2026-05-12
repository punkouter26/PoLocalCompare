#!/usr/bin/env pwsh
# setup.ps1 — One-command local dev setup for LLM Duel Arena
# Run from repo root: .\setup.ps1

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "╔══════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   LLM Duel Arena — Dev Setup         ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ─── 1. Start Azurite ────────────────────────────────────────────────────────
Write-Host "[1/3] Starting Azurite (local Azure Storage)..." -ForegroundColor Yellow
docker compose up -d azurite
if ($LASTEXITCODE -ne 0) {
    Write-Error "docker compose failed. Is Docker Desktop running?"
    exit 1
}

# ─── 2. Wait for Azurite to be ready ─────────────────────────────────────────
Write-Host "[2/3] Waiting for Azurite on port 10002..." -ForegroundColor Yellow
$maxAttempts = 30
$attempt     = 0
$ready       = $false

while ($attempt -lt $maxAttempts) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect("127.0.0.1", 10002)
        $tcp.Close()
        $ready = $true
        break
    } catch {
        $attempt++
        Write-Host "  ⏳ attempt $attempt/$maxAttempts…" -ForegroundColor DarkGray
        Start-Sleep -Milliseconds 1000
    }
}

if (-not $ready) {
    Write-Error "Azurite did not become ready after $maxAttempts seconds. Check Docker."
    exit 1
}
Write-Host "  ✅ Azurite is ready" -ForegroundColor Green

# ─── 3. Launch API with hot-reload ────────────────────────────────────────────
Write-Host "[3/3] Starting API (dotnet watch — hot reload active)..." -ForegroundColor Yellow
Write-Host "      Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

dotnet watch run `
    --project src/PoLocalCompare.Api/PoLocalCompare.Api.csproj `
    --launch-profile https
