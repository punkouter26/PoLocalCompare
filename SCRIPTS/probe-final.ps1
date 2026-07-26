$targets = @(
  'https://download.openmmlab.com/',
  'https://www.modelscope.cn/',
  'https://hf-mirror.com/api/models/mlc-ai/SmolLM2-360M-Instruct-q4f32_1-MLC',
  'https://huggingface.co/api/models/mlc-ai/SmolLM2-360M-Instruct-q4f32_1-MLC'
)
foreach ($t in $targets) {
  $name = ($t -split '/')[2]
  try {
    $r = Invoke-WebRequest -SkipCertificateCheck -UseBasicParsing -Uri $t -Method Head -TimeoutSec 6 -ErrorAction Stop
    Write-Output ('OK   | ' + $name + ' | ' + $r.StatusCode)
  } catch {
    $msg = ($_.Exception.Message -replace '\s+',' ')
    if ($msg.Length -gt 60) { $msg = $msg.Substring(0,60) }
    Write-Output ('FAIL | ' + $name + ' | ' + $msg)
  }
}