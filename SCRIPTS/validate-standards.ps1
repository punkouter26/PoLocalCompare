#Requires -Version 7
<#!
.SYNOPSIS
    Validates Po* repository standards that should not regress.

.DESCRIPTION
    Fails fast in CI/local setup when key identity and architecture conventions
    drift (solution naming, HTML title, assembly naming, .NET version pinning,
    strictness settings, and required architecture record).
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $failures.Add($message)
}

function Read-XmlFile([string]$path) {
    try {
        return [xml](Get-Content -Path $path -Raw -Encoding UTF8)
    }
    catch {
        Add-Failure("Unable to parse XML file: $path")
        return $null
    }
}

function Get-XmlFirstValue([xml]$xml, [string]$xPath) {
    if ($null -eq $xml) {
        return $null
    }

    $node = $xml.SelectSingleNode($xPath)
    if ($null -eq $node) {
        return $null
    }

    return $node.InnerText
}

Write-Host "==> Validating Po* development standards" -ForegroundColor Cyan

$directoryBuildPropsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $directoryBuildPropsPath)) {
    Add-Failure('Missing root Directory.Build.props file.')
}

$poSolutionName = $null
$buildPropsXml = if (Test-Path $directoryBuildPropsPath) { Read-XmlFile $directoryBuildPropsPath } else { $null }
if ($buildPropsXml) {
    $poSolutionName = Get-XmlFirstValue $buildPropsXml '/Project/PropertyGroup/PoSolutionName'
    if ([string]::IsNullOrWhiteSpace($poSolutionName)) {
        Add-Failure('Directory.Build.props must define <PoSolutionName>.')
    }

    $warningsAsErrors = Get-XmlFirstValue $buildPropsXml '/Project/PropertyGroup/TreatWarningsAsErrors'
    if (-not [string]::Equals($warningsAsErrors, 'true', [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure('Directory.Build.props must set <TreatWarningsAsErrors>true</TreatWarningsAsErrors>.')
    }

    $nullable = Get-XmlFirstValue $buildPropsXml '/Project/PropertyGroup/Nullable'
    if (-not [string]::Equals($nullable, 'enable', [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure('Directory.Build.props must set <Nullable>enable</Nullable>.')
    }
}

$solutionFile = Get-ChildItem -Path $repoRoot -Filter '*.slnx' -File | Select-Object -First 1
if (-not $solutionFile) {
    Add-Failure('Missing .slnx solution file in repository root.')
}
else {
    $solutionName = [System.IO.Path]::GetFileNameWithoutExtension($solutionFile.Name)
    if (-not $solutionName.StartsWith('Po', [System.StringComparison]::Ordinal)) {
        Add-Failure("Solution name must use Po* prefix. Found: $solutionName")
    }

    if (-not [string]::IsNullOrWhiteSpace($poSolutionName) -and
        -not [string]::Equals($solutionName, $poSolutionName, [System.StringComparison]::Ordinal)) {
        Add-Failure("<PoSolutionName> ($poSolutionName) must match solution name ($solutionName).")
    }
}

$globalJsonPath = Join-Path $repoRoot 'global.json'
if (-not (Test-Path $globalJsonPath)) {
    Add-Failure('Missing global.json in repository root.')
}
else {
    try {
        $globalJson = Get-Content -Path $globalJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $sdkVersion = [string]$globalJson.sdk.version
        if ([string]::IsNullOrWhiteSpace($sdkVersion) -or -not $sdkVersion.StartsWith('10.')) {
            Add-Failure("global.json sdk.version must target .NET 10.x. Found: $sdkVersion")
        }
    }
    catch {
        Add-Failure('Unable to parse global.json as JSON.')
    }
}

$architecturePath = Join-Path $repoRoot 'architecture.md'
if (-not (Test-Path $architecturePath)) {
    Add-Failure('Missing required root architecture.md architecture record.')
}

$indexPath = Join-Path $repoRoot 'src\Client\PoLocalCompare.Client\wwwroot\index.html'
if (-not (Test-Path $indexPath)) {
    Add-Failure('Missing client index.html file for title validation.')
}
else {
    $indexContent = Get-Content -Path $indexPath -Raw -Encoding UTF8
    $titleMatch = [regex]::Match($indexContent, '<title>\s*([^<]+?)\s*</title>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $titleMatch.Success) {
        Add-Failure('index.html must contain a <title> element.')
    }
    elseif (-not [string]::IsNullOrWhiteSpace($poSolutionName) -and
        -not [string]::Equals($titleMatch.Groups[1].Value.Trim(), $poSolutionName, [System.StringComparison]::Ordinal)) {
        Add-Failure("index.html <title> must match <PoSolutionName>. Found '$($titleMatch.Groups[1].Value.Trim())', expected '$poSolutionName'.")
    }
}

$navMenuPath = Join-Path $repoRoot 'src\Client\PoLocalCompare.Client\Layout\NavMenu.razor'
if ((Test-Path $navMenuPath) -and -not [string]::IsNullOrWhiteSpace($poSolutionName)) {
    $navContent = Get-Content -Path $navMenuPath -Raw -Encoding UTF8
    $expectedBrandMarkup = '<span class="brand-text">' + $poSolutionName + '</span>'
    if (-not $navContent.Contains($expectedBrandMarkup)) {
        Add-Failure('Top-nav brand text must match <PoSolutionName>.')
    }
}

$clientCsprojPath = Join-Path $repoRoot 'src\Client\PoLocalCompare.Client\PoLocalCompare.Client.csproj'
if (Test-Path $clientCsprojPath) {
    $clientCsproj = Read-XmlFile $clientCsprojPath
    if ($clientCsproj) {
        $clientCsprojRaw = Get-Content -Path $clientCsprojPath -Raw -Encoding UTF8
        $hasUnconditionalSimd = $clientCsprojRaw -match '<WasmEnableSIMD>\s*true\s*</WasmEnableSIMD>'
        $hasReleaseSimd = $clientCsprojRaw -match '<WasmEnableSIMD\s+Condition="''\$\(Configuration\)''\s*==\s*''Release''"\s*>\s*true\s*</WasmEnableSIMD>'

        if (-not ($hasUnconditionalSimd -or $hasReleaseSimd)) {
            Add-Failure('Client csproj must enable SIMD either unconditionally or for Release builds.')
        }

        $aot = Get-XmlFirstValue $clientCsproj '/Project/PropertyGroup/RunAOTCompilation'
        if (-not [string]::Equals($aot, 'false', [System.StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure('Client csproj must set <RunAOTCompilation>false</RunAOTCompilation>.')
        }
    }
}

$projectFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter '*.csproj' -File
foreach ($project in $projectFiles) {
    $xml = Read-XmlFile $project.FullName
    if (-not $xml) {
        continue
    }

    $assemblyName = Get-XmlFirstValue $xml '/Project/PropertyGroup/AssemblyName'
    if ([string]::IsNullOrWhiteSpace($assemblyName)) {
        $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
    }

    if (-not [string]::IsNullOrWhiteSpace($poSolutionName) -and
        -not $assemblyName.StartsWith($poSolutionName, [System.StringComparison]::Ordinal)) {
        Add-Failure("Assembly name '$assemblyName' in '$($project.Name)' must start with '$poSolutionName'.")
    }
}

if ($failures.Count -gt 0) {
    Write-Host "" 
    Write-Host "Standards validation failed:" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host "All standards checks passed." -ForegroundColor Green
exit 0