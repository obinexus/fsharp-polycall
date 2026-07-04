# F# adapter

The implementation lives in `FSharpPolycall/Polycall.fs`. It crosses the
P/Invoke/FFI boundary only through:

    status = polycall_ffi_run_config("fsharp-polycallrc", /*run=*/1)

`runConfig` returns the status, `tryRunConfig` returns an F# `Result`, and
`runConfigOrRaise` raises `PolycallException`. No configuration parsing or core
runtime logic belongs in this binding.
