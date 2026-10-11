param([string]$Configuration = 'Release')
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
Invoke-Dotnet -Arguments @('build', "$root\ScreenTimeTracker.slnx", '-c', $Configuration)

foreach ($test in @("tests\ScreenTimeTracker.SecurityTests", "tests\ScreenTimeTracker.ArchitectureTests", "tests\BuildingBlocks\ScreenTimeTracker.BuildingBlocks.UnitTests")) {
  $name = Split-Path $test -Leaf
  Invoke-Dotnet -Arguments @("$root\$test\bin\$Configuration\net10.0\$name.dll")

}
$ui = "$root\src\Hosts\ScreenTimeTracker.Native\bin\$Configuration\net10.0-windows\ScreenTimeTracker.Native.exe"
$process = Start-Process $ui -ArgumentList '--smoke-test' -PassThru
if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'WPF smoke test timed out.' }
$result = Get-Content "$(Split-Path $ui)\smoke-results\result.json" -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $result.passed) { throw 'WPF smoke test failed.' }
$result
