$canonicals = @(
  @{ u='https://huggingface.co/';                       n='hf.co' },
  @{ u='https://hf-mirror.com/';                        n='hf-mirror.com' },
  @{ u='https://cdn-lfs.huggingface.co/';                n='cdn-lfs' },
  @{ u='https://cas-bridge.xethub.hf.co/';                n='hf-cas-bridge' },
  @{ u='https://download-cdn.huggingface.co/';            n='hf-download-cdn' },
  @{ u='https://github.com/mlc-ai/';                      n='github-mlc' },
  @{ u='https://gcr.io/';                                 n='gcr.io' },
  @{ u='https://mlc-artifacts.s3.us-west-2.amazonaws.com/'; n='mlc-s3' },
  @{ u='https://hub.modelz.co/';                          n='modelz.hub' },
  @{ u='https://registry.ollama.ai/';                      n='ollama-registry' },
  @{ u='https://www.nuget.org/';                          n='nuget' },
  @{ u='https://registry.npmjs.org/';                     n='npm' },
  @{ u='https://pypi.org/';                               n='pypi' }
)
$outFile = 'c:\Users\punko\Downloads\PoLocalCompare\.mirror-probe.txt'
'HTTP_PROBE_BEGIN' | Set-Content -Path $outFile -Encoding utf8
foreach($h in $canonicals){
  $line = ''
  try {
    $r = Invoke-WebRequest -SkipCertificateCheck -UseBasicParsing -Uri $h.u -Method Head -TimeoutSec 6 -ErrorAction Stop
    $line = ('OK   | ' + $h.n + ' | ' + $r.StatusCode)
  } catch {
    $msg = ($_.Exception.Message -replace '\s+',' ')
    if ($msg.Length -gt 80) { $msg = $msg.Substring(0,80) }
    $line = ('FAIL | ' + $h.n + ' | ' + $msg)
  }
  Add-Content -Path $outFile -Value $line
  Write-Output $line
}
'HTTP_PROBE_END' | Add-Content -Path $outFile