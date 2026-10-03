#!/bin/sh
# Build and run the fsharp-polycall tests against the REAL installed libpolycall.
#
#   POLYCALL_LIBRARY=/opt/polycall/lib/libpolycall.so.1 \
#   POLYCALL_CLI=/opt/polycall/bin/polycall sh scripts/test.sh [expecto args]
#
# Exit status: 0 = all tests ran and passed, 1 = failure,
# 77 = SKIP (the .NET 8 SDK is missing; nothing was tested).
set -u
cd "$(dirname "$0")/.." || exit 1

skip() { echo "SKIP: $*"; exit 77; }

command -v dotnet >/dev/null 2>&1 || skip "dotnet not found (.NET 8 SDK required)"
dotnet --list-sdks 2>/dev/null | grep -Eq '^([89]|[1-9][0-9])\.' \
  || skip "no .NET SDK >= 8 installed (dotnet --list-sdks is empty)"

if [ -z "${POLYCALL_CLI:-}" ] && command -v polycall >/dev/null 2>&1; then
  POLYCALL_CLI=$(command -v polycall); export POLYCALL_CLI
fi

# clearly-labelled fake libraries for the loader tests only (ABI 2, old 1.0 core)
if command -v cc >/dev/null 2>&1; then
  mkdir -p build/fake
  if [ -z "${POLYCALL_TEST_FAKE_ABI2:-}" ] &&
     cc -shared -fPIC -o build/fake/libfake_polycall_abi2.so tests/fixtures/fake_polycall.c; then
    POLYCALL_TEST_FAKE_ABI2=$(pwd)/build/fake/libfake_polycall_abi2.so; export POLYCALL_TEST_FAKE_ABI2
  fi
  if [ -z "${POLYCALL_TEST_FAKE_V10:-}" ] &&
     cc -shared -fPIC -DFAKE_V10 -o build/fake/libfake_polycall_v10.so tests/fixtures/fake_polycall.c; then
    POLYCALL_TEST_FAKE_V10=$(pwd)/build/fake/libfake_polycall_v10.so; export POLYCALL_TEST_FAKE_V10
  fi
fi

FSHARP_POLYCALL_REPO=$(pwd); export FSHARP_POLYCALL_REPO
echo "dotnet: $(dotnet --version)"
echo "POLYCALL_LIBRARY=${POLYCALL_LIBRARY:-<platform default>} POLYCALL_CLI=${POLYCALL_CLI:-<none>}"
dotnet build src/FSharpPolycall.Cli/FSharpPolycall.Cli.fsproj -c Release || exit 1
FSHARP_POLYCALL_CLI=$(pwd)/src/FSharpPolycall.Cli/bin/Release/net8.0/fsharp-polycall.dll; export FSHARP_POLYCALL_CLI
exec dotnet run --project tests/FSharpPolycall.Tests/FSharpPolycall.Tests.fsproj -c Release -- --summary --colours 0 --no-spinner "$@"
