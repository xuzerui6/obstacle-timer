$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskManifest=Get-Content -LiteralPath (Join-Path $taskRoot 'web\phone\vision-assets.json') -Raw | ConvertFrom-Json
foreach($taskEntry in $taskManifest.PSObject.Properties){
 $taskFile=Join-Path $taskRoot ('web\phone\public\vision-assets\'+$taskEntry.Name)
 if(!(Test-Path -LiteralPath $taskFile)){throw "Missing local model asset: $($taskEntry.Name). Run scripts/fetch-vision-assets.py first."}
 if((Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskEntry.Value.sha256){throw "Vision asset hash mismatch: $($taskEntry.Name)"}
}
Write-Host 'Verified 5 pinned local BodyPix/WASM assets.'
