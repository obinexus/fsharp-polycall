<#
.SYNOPSIS
  Run the fsharp-polycall tests on Windows against an installed Polycall core.

.DESCRIPTION
  Runs the Expecto suite with the .NET 8 runtime against the REAL library of
  one Windows core build: the MSVC build (bin\polycall.dll) or the MinGW /
  MSYS2 UCRT64 build (bin\libpolycall.dll), plus that build's polycall.exe
  for the RPC and interop checks.

  With a .NET 8+ SDK installed the test project and CLI are built here.
  Without an SDK, pass -BuildDir: a portable (framework-dependent) build made
  elsewhere with
      dotnet publish tests/FSharpPolycall.Tests -c Release -o <dir>/tests
      dotnet publish src/FSharpPolycall.Cli     -c Release -o <dir>/cli
  (the output is IL, identical on every OS). Neither -> exit 77 (SKIP).

  The fake ABI-2 / 1.0 libraries for the loader tests are built from
  tests\fixtures\fake_polycall.c when gcc (MinGW/UCRT64) is on PATH or given
  with -Gcc; otherwise those two checks are reported as ignored, never passed.

.EXAMPLE
  scripts\test-windows.ps1 -Core C:\polycall\windows-msvc-x64
  scripts\test-windows.ps1 -Core C:\polycall\windows-ucrt64-x64 -BuildDir C:\build\fsharp -Gcc C:\msys64\ucrt64\bin\gcc.exe
#>
param(
    [Parameter(Mandatory = $true)][string]$Core,
    [string]$Library,
    [string]$Cli,
    [string]$BuildDir,
    [string]$Gcc,
    [string]$JunitSummary,
    [string[]]$ExpectoArgs = @()
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Library) {
    $Library = @('polycall.dll', 'libpolycall.dll') | ForEach-Object { Join-Path $Core "bin\$_" } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Library -or -not (Test-Path $Library)) { throw "no polycall.dll / libpolycall.dll under $Core\bin" }
if (-not $Cli) { $Cli = Join-Path $Core 'bin\polycall.exe' }

$hasSdk = $false
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $hasSdk = [bool](& dotnet --list-sdks 2>$null | Where-Object { $_ -match '^([89]|[1-9][0-9])\.' })
}
if ($hasSdk -and -not $BuildDir) {
    $BuildDir = Join-Path $repo 'build\windows'
    & dotnet publish (Join-Path $repo 'tests\FSharpPolycall.Tests\FSharpPolycall.Tests.fsproj') -c Release -o (Join-Path $BuildDir 'tests')
    if ($LASTEXITCODE -ne 0) { exit 1 }
    & dotnet publish (Join-Path $repo 'src\FSharpPolycall.Cli\FSharpPolycall.Cli.fsproj') -c Release -o (Join-Path $BuildDir 'cli')
    if ($LASTEXITCODE -ne 0) { exit 1 }
} elseif (-not $BuildDir) {
    Write-Output 'SKIP: no .NET SDK >= 8 (dotnet --list-sdks is empty) and no -BuildDir given; nothing was tested'
    exit 77
}
$testDll = Join-Path $BuildDir 'tests\FSharpPolycall.Tests.dll'
if (-not (Test-Path $testDll)) { throw "missing $testDll" }

$env:POLYCALL_LIBRARY = (Resolve-Path $Library).Path
$env:POLYCALL_CLI = (Resolve-Path $Cli).Path
$env:FSHARP_POLYCALL_REPO = $repo
$env:POLYCALL_TELEMETRY = 'off'
if (-not $env:POLYCALL_DEV_TOKEN) { $env:POLYCALL_DEV_TOKEN = 'fs-test-' + [guid]::NewGuid().ToString('N') }
$cliDll = Join-Path $BuildDir 'cli\fsharp-polycall.dll'
if (Test-Path $cliDll) { $env:FSHARP_POLYCALL_CLI = $cliDll }

if (-not $Gcc) { $g = Get-Command gcc -ErrorAction SilentlyContinue; if ($g) { $Gcc = $g.Source } }
if ($Gcc) {
    # gcc's cc1/as/ld need the toolchain's DLL directory; restored afterwards so
    # the tests do not run with MinGW runtime DLLs on PATH
    $savedPath = $env:PATH
    $env:PATH = (Split-Path -Parent $Gcc) + ';' + $env:PATH
    $fake = Join-Path $repo 'build\fake'
    New-Item -ItemType Directory -Force $fake | Out-Null
    $src = Join-Path $repo 'tests\fixtures\fake_polycall.c'
    & $Gcc -shared -o (Join-Path $fake 'fake_polycall_abi2.dll') $src
    if ($LASTEXITCODE -eq 0) { $env:POLYCALL_TEST_FAKE_ABI2 = Join-Path $fake 'fake_polycall_abi2.dll' }
    & $Gcc -shared -DFAKE_V10 -o (Join-Path $fake 'fake_polycall_v10.dll') $src
    if ($LASTEXITCODE -eq 0) { $env:POLYCALL_TEST_FAKE_V10 = Join-Path $fake 'fake_polycall_v10.dll' }
    $env:PATH = $savedPath
}

Write-Output "dotnet runtime: $((& dotnet --list-runtimes) -match 'NETCore.App 8' -join ', ')"
Write-Output "POLYCALL_LIBRARY=$env:POLYCALL_LIBRARY POLYCALL_CLI=$env:POLYCALL_CLI"
$runArgs = @($testDll, '--summary', '--colours', '0', '--no-spinner')
if ($JunitSummary) { $runArgs += @('--junit-summary', $JunitSummary) }
& dotnet @runArgs @ExpectoArgs
exit $LASTEXITCODE
