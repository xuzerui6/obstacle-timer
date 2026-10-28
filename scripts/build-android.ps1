param([string]$JavaHome, [string]$AndroidSdk, [string]$Gradle)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if(!$JavaHome) { $JavaHome = Get-ChildItem -LiteralPath (Join-Path $taskRoot '.tools\java') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName }
if(!$JavaHome) { $JavaHome = $env:JAVA_HOME }
if(!$AndroidSdk) { $AndroidSdk = Join-Path $taskRoot '.tools\android-sdk' }
if(!$Gradle) { $Gradle = Join-Path $taskRoot '.tools\gradle-8.9\bin\gradle.bat' }
$env:JAVA_HOME = $JavaHome
$env:ANDROID_HOME = $AndroidSdk
$env:PATH = "$JavaHome\bin;" + $env:PATH
& $Gradle --no-daemon -p (Join-Path $taskRoot 'mobile\android') assembleDebug lintDebug --console=plain
if($LASTEXITCODE -ne 0) { throw 'Android build or lint failed' }
New-Item -ItemType Directory -Path (Join-Path $taskRoot 'artifacts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'mobile\android\app\build\outputs\apk\debug\app-debug.apk') -Destination (Join-Path $taskRoot 'artifacts\ObstacleFinish-0.1.0-android-test.apk') -Force
Write-Host 'Debug-signed test APK created; physical camera/precision validation is still required.'
