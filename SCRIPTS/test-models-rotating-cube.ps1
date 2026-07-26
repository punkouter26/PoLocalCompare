$ErrorActionPreference = 'Stop'

$base = 'https://localhost:5001'
$headers = @{ 'X-Fake-User' = 'copilot-test'; 'X-Fake-Roles' = 'User' }
$prompt = 'rotating cube'
$outPath = 'c:\Users\punko\Downloads\PoLocalCompare\model-test-status.tsv'

Set-Content -Path $outPath -Value 'MODEL|TYPE|STATUS|TOKENS|DURATION_MS|DUEL_ID|FAILURE_REASON' -Encoding utf8

$models = Invoke-RestMethod -SkipCertificateCheck -Uri "$base/api/models" -Headers $headers -Method Get
$serverModels = @($models | Where-Object { $_.modelType -ne 'Local' })

if ($serverModels.Count -lt 2)
{
    throw "Need at least 2 non-Local models to run duel-based tests. Found: $($serverModels.Count)"
}

$control = $serverModels[0]
$secondary = $serverModels[1]

foreach ($model in $serverModels)
{
    $opponent = if ($model.modelId -eq $control.modelId) { $secondary } else { $control }

    $body = @{
        leftModelId = $model.modelId
        rightModelId = $opponent.modelId
        promptText = $prompt
    } | ConvertTo-Json

    $duel = Invoke-RestMethod -SkipCertificateCheck -Uri "$base/api/duels" -Headers $headers -Method Post -ContentType 'application/json' -Body $body
    $duelId = $duel.duelId

    $result = $null
    for ($i = 0; $i -lt 300; $i++)
    {
        $duelState = Invoke-RestMethod -SkipCertificateCheck -Uri "$base/api/duels/$duelId" -Headers $headers -Method Get
        $hit = @($duelState.results | Where-Object { $_.modelId -eq $model.modelId })
        if ($hit.Count -gt 0)
        {
            $result = $hit[0]
            break
        }
    }

    if ($null -eq $result)
    {
        Add-Content -Path $outPath -Value ("{0}|{1}|NO_RESULT|0|0|{2}|No result returned" -f $model.displayName, $model.modelType, $duelId)
        continue
    }

    $status = if ([bool]$result.isFailure) { 'FAILS' } else { 'WORKS' }
    $reason = if ([string]::IsNullOrWhiteSpace($result.failureReason)) { '-' } else { $result.failureReason.Replace('|', '/').Replace("`r", ' ').Replace("`n", ' ') }

    Add-Content -Path $outPath -Value ("{0}|{1}|{2}|{3}|{4}|{5}|{6}" -f $model.displayName, $model.modelType, $status, $result.tokenCount, $result.totalDurationMs, $duelId, $reason)
}

Add-Content -Path $outPath -Value 'LOCAL_BROWSER_ONLY_MODELS'
$models | Where-Object { $_.modelType -eq 'Local' } | Sort-Object displayName | ForEach-Object {
    Add-Content -Path $outPath -Value ("{0}|{1}|NOT_TESTED_BROWSER_REQUIRED" -f $_.displayName, $_.modelType)
}

Write-Output "WROTE: $outPath"