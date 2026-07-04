$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$nativeSource = Join-Path $root 'c_src/fsharp_polycall.c'
$fsharpSource = Join-Path $root 'src/FSharpPolycall/Polycall.fs'
$forbidden = 'fopen|open\(|CreateFile|sscanf|strtok|socket\(|connect\('
$matches = Select-String -Path $nativeSource,$fsharpSource -Pattern $forbidden

if ($matches) {
    $matches | ForEach-Object { Write-Error $_.Line }
    throw 'fsharp-polycall must not parse configuration or implement runtime logic'
}

$adapter = Get-Content -Raw $nativeSource
$fsharp = Get-Content -Raw $fsharpSource
if (-not $adapter.Contains('polycall_ffi_run_config(config_path, 1)')) {
    throw 'fsharp-polycall does not forward through polycall_ffi_run_config'
}
if (-not $fsharp.Contains('DllImport')) {
    throw 'fsharp-polycall does not declare a P/Invoke boundary'
}
if (-not $fsharp.Contains('UnmanagedType.LPUTF8Str')) {
    throw 'fsharp-polycall does not marshal config paths as UTF-8'
}

Write-Output 'fsharp-polycall thin-adapter check: PASS'
