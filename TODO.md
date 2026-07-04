# TODO — fsharp-polycall

Status: implemented thin F#/.NET adapter for libpolycall 1.5.

- [x] Publishable `@obinexusltd/fsharp-polycall` npm source package
- [x] F# status, `Result`, and exception APIs
- [x] UTF-8 P/Invoke declaration and exported native adapter
- [x] Exact `polycall_ffi_run_config(config_path, 1)` forwarding
- [x] Runnable example under `examples/`
- [x] Native forwarding test and F#/PInvoke smoke project
- [x] Thin-adapter source audit for Windows and POSIX shells
- [ ] Exercise the F# smoke project in release CI across supported platforms
- [ ] Publish signed platform-native artifacts alongside the source package

Do not add configuration parsing or runtime policy here; adapt the core only.
