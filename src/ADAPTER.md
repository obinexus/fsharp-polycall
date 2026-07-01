# F# adapter (scaffold)

Implement the F# adapter here. It must call across the FFI boundary only:

    status = polycall_ffi_run_config("fsharp-polycallrc", /*run=*/1)

Return/raise a F#-native error when `status` is non-zero. Do not parse
config or duplicate any core logic. See ../../../docs/adapter-pattern.md.
