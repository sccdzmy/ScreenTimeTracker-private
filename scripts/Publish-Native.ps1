param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts\native-win-x64'), [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path ${env:ProgramW6432} 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'Install the .NET 10 SDK before running this script.' }
function Invoke-Dotnet {
  param([string[]]$Arguments)
  # Start-Process also works when invoked from a WSL-launched PowerShell session.
  $quoted = $Arguments | ForEach-Object { '"' + $_ + '"' }
  $process = Start-Process -FilePath $dotnet -ArgumentList $quoted -NoNewWindow -Wait -PassThru
  if ($process.ExitCode -ne 0) { throw "dotnet exited with code $($process.ExitCode)." }
}
$root = Split-Path $PSScriptRoot -Parent
$Output = [IO.Path]::GetFullPath($Output)
$contained = if ($SelfContained) { 'true' } else { 'false' }
# Publish separate processes into one package without combining their dependencies or entrypoints.
Invoke-Dotnet -Arguments @('publish', "$root\src\Hosts\ScreenTimeTracker.Desktop\ScreenTimeTracker.Desktop.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', $contained, '-o', $Output, '-p:LegacyWebUI=false')

Invoke-Dotnet -Arguments @('publish', "$root\src\Hosts\ScreenTimeTracker.Native\ScreenTimeTracker.Native.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', $contained, '-o', $Output)

if ((Test-Path "$Output\Photino.NET.dll") -or (Test-Path "$Output\wwwroot")) { throw 'Output contains legacy assets. Use a clean output directory.' }
Write-Output "Run $Output\ScreenTimeTracker.exe; the native UI opens from its tray icon."
