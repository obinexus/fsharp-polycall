#!/usr/bin/env sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

if grep -E -n 'fopen|open\(|CreateFile|sscanf|strtok|socket\(|connect\(' \
    "$root/c_src/fsharp_polycall.c" "$root/src/FSharpPolycall/Polycall.fs"; then
    echo "fsharp-polycall must not parse configuration or implement runtime logic" >&2
    exit 1
fi

grep -F -q 'polycall_ffi_run_config(config_path, 1)' \
    "$root/c_src/fsharp_polycall.c"
grep -F -q 'DllImport' "$root/src/FSharpPolycall/Polycall.fs"
grep -F -q 'UnmanagedType.LPUTF8Str' "$root/src/FSharpPolycall/Polycall.fs"

echo "fsharp-polycall thin-adapter check: PASS"
