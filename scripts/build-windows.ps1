param([string]$Dotnet, [switch]$SkipTests, [switch]$FrameworkDependent, [switch]$SkipWeb)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if(!$Dotnet) {
 $taskLocal = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
 $Dotnet = if(Test-Path -LiteralPath $taskLocal) { $taskLocal } else { 'dotnet' }
}
Push-Location $taskRoot
try {
 if(!$SkipWeb) { & "$PSScriptRoot\build-web.ps1" }
 if(!(Test-Path -LiteralPath 'vendor\timy\AlgeTimyUsb.x64.dll')) { & "$PSScriptRoot\import-timy-sdk.ps1" }
 & $Dotnet build 'src\ObstacleTimer.TimyBridge\ObstacleTimer.TimyBridge.csproj' -c Release
 if($LASTEXITCODE -ne 0) { throw 'Bridge build failed' }
 if(!$SkipTests) {
  & $Dotnet run --project 'tests\ObstacleTimer.Tests\ObstacleTimer.Tests.csproj' -c Release
  if($LASTEXITCODE -ne 0) { throw 'Tests failed' }
 }
 $taskPublish = Join-Path $taskRoot 'artifacts\windows'
 $taskSelfContained = if($FrameworkDependent) {'false'} else {'true'}
 & $Dotnet publish 'src\ObstacleTimer.Desktop\ObstacleTimer.Desktop.csproj' -c Release -r win-x64 --self-contained $taskSelfContained -o $taskPublish -p:PublishSingleFile=false
 if($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
 New-Item -ItemType Directory -Path (Join-Path $taskPublish 'bridge') -Force | Out-Null
 Copy-Item -Path 'src\ObstacleTimer.TimyBridge\bin\Release\net48\*' -Destination (Join-Path $taskPublish 'bridge') -Force
 $taskDummy = Get-ChildItem -LiteralPath $taskPublish -Recurse -Filter '*Dummy*'
 if($taskDummy) { throw 'Dummy SDK assembly must never be deployed' }
 Copy-Item -LiteralPath 'README.md','docs\操作指南.md' -Destination $taskPublish -Force
 Copy-Item -LiteralPath 'docs' -Destination $taskPublish -Recurse -Force
 $taskInno = Join-Path $taskRoot '.tools\inno\ISCC.exe'
 if(Test-Path -LiteralPath $taskInno) { & $taskInno 'scripts\installer.iss'; if($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' } }
 Write-Host "Windows app: $taskPublish\ObstacleTimer.exe"
} finally { Pop-Location }
