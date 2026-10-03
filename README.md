# fsharp-polycall

F#/.NET binding for the [Polycall](https://github.com/obinexus/polycall) core
library, **binding ABI v1** (`polycall.h`, documented in the core's
`docs/BINDING_ABI.md`).

The binding P/Invokes `polycall.dll` (Windows, MSVC build), `libpolycall.dll`
(MinGW) or `libpolycall.so.1` (Linux) directly: `DllImport` with explicit
`CallingConvention.Cdecl`, strings as NUL-terminated UTF-8 byte arrays,
outputs in caller-owned byte buffers, `size_t` as `unativeint`. There is no C
glue library to build.

## Requirements

- .NET 8 (SDK to build; the runtime is enough to run a built app)
- libpolycall >= 1.1.0 (binding ABI 1), 64-bit

## Loading the library

A `NativeLibrary.SetDllImportResolver` resolves the library in this order:

1. `POLYCALL_LIBRARY` environment variable (full path), then
2. `polycall.dll`, `libpolycall.dll` (Windows), `libpolycall.so.1` (Linux),
   `libpolycall.1.dylib` (macOS) through the OS search.

All 20 symbols are verified up front and `polycall_ffi_abi_version()` must
be 1. A missing library, a missing symbol (a 1.0 core) or an ABI mismatch
raises `PolycallLoadException`, never a crash.

## API (`OBINexus.Polycall`)

```fsharp
open OBINexus.Polycall

Polycall.abiVersion ()                       // 1
Polycall.version ()                          // "1.1.0"
Polycall.runConfig "fsharp-polycallrc"       // polycall_ffi_run_config(path, 1) -> status (0 = ok)
Polycall.tryRunConfig path                   // Ok () | Error status
Polycall.runConfigOrRaise path               // raises PolycallException (status, name, detail)
Polycall.validate path                       // polycall_ffi_run_config(path, 0)
Polycall.describe path                       // JSON
Polycall.call "127.0.0.1:7000" "inventory" "get" """{"item_id":"widget-a"}""" 2000u

use alpha = Peer.Open("alpha", "127.0.0.1:0", token)
use beta = Peer.Open("beta", "127.0.0.1:0", token)
alpha.Register("beta", beta.Endpoint)
alpha.Send("beta", payload, "msg-1", 5000u)  // exactly one delivery attempt
let m = beta.Recv 5000u                      // m.Sender, m.MessageId, m.Payload
```

`Peer` covers Open / Close / Endpoint / NodeId / Register / Unregister / List /
Ping / Send / SendText / Recv / RecvInto / TryRecv / Cancel / Health. Its
handle is owned by a `SafeHandle` (idempotent, finalizer-safe disposal); the
raw int handle is what crosses P/Invoke, so `Close()` from another thread
wakes a blocked `Recv` with `PolycallStatus.Closed`. Methods of a closed peer
pass its stale handle to the library, which answers `InvalidHandle`.

`PolycallException` carries `Status` / `StatusCode` (`PolycallStatus`),
`StatusName` (`polycall_strerror`), `Detail` (`polycall_last_error`),
`RemoteError` (runtime error object from `call`) and `RequiredSize`.

## Command line / interop agent

`src/FSharpPolycall.Cli` builds `fsharp-polycall`:

```
fsharp-polycall version
fsharp-polycall validate [--lenient] FILE
fsharp-polycall call SERVICE OPERATION --endpoint H:P [--input JSON] [--timeout-ms N]
fsharp-polycall peer echo --node-id ID [--endpoint H:P] [--endpoint-file F]
                          [--peer ID=H:P ...] [--count N] [--idle-timeout-ms N]
```

`peer echo` sends every received message back to its sender with id
`echo-<id>`; other bindings' tests use it for cross-language interop. The
shared token comes from `POLYCALL_DEV_TOKEN`, never argv.

## Tests

The Expecto suite (`tests/FSharpPolycall.Tests`) runs against the real
library and the real `polycall` CLI:

```sh
POLYCALL_LIBRARY=/opt/polycall/lib/libpolycall.so.1 \
POLYCALL_CLI=/opt/polycall/bin/polycall sh scripts/test.sh
```

On Windows, against the MSVC build (`polycall.dll`) or the MinGW/UCRT64 build
(`libpolycall.dll`) of the core:

```powershell
scripts\test-windows.ps1 -Core C:\polycall\windows-msvc-x64
scripts\test-windows.ps1 -Core C:\polycall\windows-ucrt64-x64 -BuildDir C:\build\fsharp -Gcc C:\msys64\ucrt64\bin\gcc.exe
```

Without a .NET SDK the script runs a portable build made elsewhere with
`dotnet publish` (the IL is the same on every OS; only the .NET 8 runtime is
needed to run it).

It covers the BINDING_ABI.md checklist (version/ABI, run_config including a
non-ASCII directory and file name, call against `polycall start` and
`polycall daemon start`, two-node exchange both ways with
empty/UTF-8/binary/1 MiB/1 MiB+1 payloads, registry ownership, duplicates,
auth, dead peer, timeouts, too-small buffers, cancel/close, invalid handles,
concurrent senders and calls, SafeHandle finalization), loader failures
(missing library, an old 1.0 library, ABI 2 — also in a fresh CLI process)
and interop with `polycall peer serve/send/recv` (1 MiB both ways). With
`POLYCALL_INTEROP_ECHO` set to another binding's echo agent (e.g.
`java ... org.obinexus.polycall.cli.Main`) it also exchanges payloads with
that binding. Checks that cannot run are reported as ignored, never as
passed; the scripts exit 77 when no .NET SDK (or portable build) exists.

`scripts/test-package.sh` packs `OBINexus.FSharpPolycall` into a local
directory (never published) and builds and runs a clean consumer project that
restores it from there against the real library.

`tests/fixtures/fake_polycall.c` builds clearly-labelled fake libraries (ABI
2, and an old 1.0 core without the ABI v1 symbols) used only to prove that the
loader refuses them.

## License

MIT, see [LICENSE](LICENSE).
