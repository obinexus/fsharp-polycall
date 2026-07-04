# @obinexusltd/fsharp-polycall

F#/.NET P/Invoke binding for
[libpolycall](https://github.com/obinexus/libpolycall) 1.5. The adapter maps
F# calls to the single core entry point:

```c
polycall_ffi_run_config(config_path, 1)
```

Configuration parsing, validation, networking, and runtime policy remain in
libpolycall. This project only marshals a UTF-8 configuration path and returns
the core status unchanged.

## Install the source package

```shell
npm install @obinexusltd/fsharp-polycall
```

The npm package publishes the complete F#, P/Invoke, and C source tree. It is a
native source distribution rather than a JavaScript implementation. Calling
`require('@obinexusltd/fsharp-polycall')` returns absolute paths to the packaged
project, sources, headers, configuration, manifest, and Makefile.

## Requirements

- libpolycall 1.5 development library and headers
- .NET 8 SDK with F# support
- a C11 compiler and GNU Make

## Build

Build the standalone adapter archive without linking libpolycall:

```shell
make
```

Build the F# assembly:

```shell
dotnet build src/FSharpPolycall/FSharpPolycall.fsproj --configuration Release
# or
npm run build:dotnet
```

Build the native P/Invoke library by supplying the linker flags for
libpolycall:

```shell
export POLYCALL_LDFLAGS='-L/path/to/lib -lpolycall'
make native
```

PowerShell uses the same variable:

```powershell
$env:POLYCALL_LDFLAGS = '-LC:\path\to\lib -lpolycall'
make native
```

Place `fsharp_polycall.dll`, `libfsharp_polycall.so`, or
`libfsharp_polycall.dylib` in a native-library search directory before running
the F# assembly. .NET resolves the extension and Unix `lib` prefix from the
P/Invoke name automatically.

## API

```fsharp
open OBINexus.Polycall

let status = Polycall.runConfig "fsharp-polycallrc"
let result = Polycall.tryRunConfig "fsharp-polycallrc"
Polycall.runConfigOrRaise "fsharp-polycallrc"
```

- `runConfig` returns the exact libpolycall status.
- `tryRunConfig` produces `Ok ()` or `Error status`.
- `runConfigOrRaise` raises `PolycallException` for a non-zero status.
- `runDefault` and `runDefaultOrRaise` use `fsharp-polycallrc`.

See [`examples/Program.fs`](examples/Program.fs) for a runnable example.

## Verification

The default suite needs only a C compiler, Make, Node.js, and PowerShell on
Windows:

```shell
npm test
```

It verifies exact path forwarding, the required validation flag, status
propagation, thin-adapter constraints, and npm package completeness.

With the .NET 8 SDK installed, run the end-to-end F#/PInvoke smoke test:

```shell
npm run test:fsharp
```

## Package layout

- `src/FSharpPolycall/` — public F# API and .NET project
- `c_src/` — native adapter exported for P/Invoke
- `include/` — adapter C header
- `generated/polycall/` — minimal generated core FFI declaration
- `examples/` — runnable F# example and sample configuration
- `tests/` — native mock, F# smoke project, and npm package test

## Author and license

Copyright © 2026 Nnamdi Michael Okpala
<okpalan@protonmail.com>.

Released under the [MIT License](LICENSE).
