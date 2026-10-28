param([string]$Archive = 'F:\timy3\software\AlgeTimyUsbDLLExample.zip')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskDestination = Join-Path $taskRoot 'vendor\timy'
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = [IO.Compression.ZipFile]::OpenRead($Archive)
try {
 foreach($taskName in @('Dependencies/AlgeTimyUsb.Dummy.dll','Dependencies/AlgeTimyUsb.x64.dll','Dependencies/AlgeTimyUsb.x86.dll','Dependencies/x64/vcruntime140.dll','Dependencies/x64/vcruntime140_1.dll')) {
  $taskEntry = $taskZip.Entries | Where-Object { $_.FullName.Replace('\','/') -eq $taskName } | Select-Object -First 1
  if(!$taskEntry) { throw "SDK entry missing: $taskName" }
  [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry, (Join-Path $taskDestination ($taskName.Split('/')[-1])), $true)
 }
} finally { $taskZip.Dispose() }
