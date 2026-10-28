param([string]$Node, [string]$Pnpm)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if(!$Node) {
 $taskCommand = Get-Command node -ErrorAction SilentlyContinue
 $Node = if($taskCommand) { $taskCommand.Source } else { Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' }
}
if(!(Test-Path -LiteralPath $Node)) { throw 'Build the web client with Node.js 22+ and pnpm, or pass -Node and -Pnpm paths.' }
if(!$Pnpm) {
 $taskCommand = Get-Command pnpm -ErrorAction SilentlyContinue
 $Pnpm = if($taskCommand) { $taskCommand.Source } else { Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\bin\fallback\pnpm.cmd' }
}
$env:PATH = (Split-Path $Node -Parent) + ';' + $env:PATH
$taskWeb = Join-Path $taskRoot 'web\phone'
if(!(Test-Path -LiteralPath (Join-Path $taskWeb 'node_modules\react\package.json'))) {
 & $Pnpm --dir $taskWeb install --frozen-lockfile
 if($LASTEXITCODE -ne 0) { throw 'Web dependency installation failed' }
}
& "$PSScriptRoot\verify-vision-assets.ps1"
& $Pnpm --dir $taskWeb run build
if($LASTEXITCODE -ne 0) { throw 'Web build failed' }
& $Pnpm --dir $taskWeb run test
if($LASTEXITCODE -ne 0) { throw 'Web tests failed' }
