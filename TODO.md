# TODO — fsharp-polycall

Status: P/Invoke binding for the Polycall binding ABI v1 (polycall >= 1.1.0).

- [x] Exact DllImport (cdecl) declarations of every Binding ABI v1 function
- [x] Loader: POLYCALL_LIBRARY, then polycall.dll / libpolycall.dll / libpolycall.so.1;
      all symbols and the ABI verified up front (PolycallLoadException)
- [x] runConfig / validate / describe / call / Peer (SafeHandle disposal)
- [x] PolycallException with status, name (polycall_strerror) and detail (polycall_last_error)
- [x] Expecto suite against the real library and `polycall` CLI (Linux + Windows)
- [x] Interop with `polycall peer serve` and with java-polycall's echo agent
- [ ] Publish the NuGet package and the npm source package (not done by QA)
- [ ] macOS run (libpolycall.1.dylib) — not tested
