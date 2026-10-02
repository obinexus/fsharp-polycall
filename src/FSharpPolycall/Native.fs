namespace OBINexus.Polycall

open System
open System.IO
open System.Runtime.InteropServices

/// The Polycall library could not be used: it was not found, it lacks a
/// Binding ABI v1 symbol (an older 1.0 core), or polycall_ffi_abi_version()
/// is not 1. Raised instead of crashing on first use.
type PolycallLoadException(library: string, reason: string, inner: exn) =
    inherit Exception(sprintf "polycall: cannot use library '%s': %s" library reason, inner)
    new(library, reason) = PolycallLoadException(library, reason, null)
    /// The library path or name that was tried.
    member _.Library = library
    /// Why it could not be used.
    member _.Reason = reason

/// Exact P/Invoke declarations of polycall.h (Binding ABI v1). Strings are
/// passed as NUL-terminated UTF-8 byte arrays, outputs as caller-owned byte
/// buffers, size_t as unativeint, handles as int32. cdecl everywhere.
module internal Native =
    [<Literal>]
    let Lib = "polycall"

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_ffi_abi_version()

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_ffi_version(byte[] buf, int len)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern nativeint polycall_strerror(int status)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_last_error(byte[] buf, unativeint cap)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern nativeint polycall_get_version()

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_ffi_run_config(byte[] configPath, int run)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_ffi_describe(byte[] configPath, byte[] buf, int len)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_call(byte[] endpoint, byte[] service, byte[] operation, byte[] inputJson,
                             uint32 timeoutMs, byte[] out, unativeint outCap, unativeint& outLen)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_open(byte[] nodeId, byte[] bindEndpoint, byte[] authToken, int& outHandle)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_close(int h)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_endpoint(int h, byte[] buf, unativeint cap)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_node_id(int h, byte[] buf, unativeint cap)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_register(int h, byte[] peerId, byte[] endpoint)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_unregister(int h, byte[] peerId)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_list(int h, byte[] buf, unativeint cap, unativeint& outLen)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_ping(int h, byte[] peer, uint32 timeoutMs)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_send(int h, byte[] peer, byte[] payload, unativeint len,
                                  byte[] messageId, uint32 timeoutMs)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_recv(int h, uint32 timeoutMs, byte[] sender, unativeint senderCap,
                                  byte[] messageId, unativeint messageIdCap,
                                  byte[] payload, unativeint payloadCap, unativeint& payloadLen)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_cancel(int h)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
    extern int polycall_peer_health(int h, byte[] buf, unativeint cap, unativeint& outLen)

    /// Every symbol the binding uses; all must resolve before the first call.
    let symbols =
        [ "polycall_ffi_abi_version"; "polycall_ffi_version"; "polycall_strerror"
          "polycall_last_error"; "polycall_get_version"; "polycall_ffi_run_config"
          "polycall_ffi_describe"; "polycall_call"; "polycall_peer_open"; "polycall_peer_close"
          "polycall_peer_endpoint"; "polycall_peer_node_id"; "polycall_peer_register"
          "polycall_peer_unregister"; "polycall_peer_list"; "polycall_peer_ping"
          "polycall_peer_send"; "polycall_peer_recv"; "polycall_peer_cancel"; "polycall_peer_health" ]

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type AbiVersionFn = delegate of unit -> int

/// Library loading: POLYCALL_LIBRARY first, then polycall.dll / libpolycall.dll
/// (Windows), libpolycall.so.1 (Linux), libpolycall.1.dylib (macOS).
module internal Loader =
    [<Literal>]
    let EnvVar = "POLYCALL_LIBRARY"

    [<Literal>]
    let ExpectedAbi = 1

    let defaultNames () =
        if OperatingSystem.IsWindows() then [ "polycall.dll"; "libpolycall.dll" ]
        elif OperatingSystem.IsMacOS() then [ "libpolycall.1.dylib" ]
        else [ "libpolycall.so.1" ]

    /// Load one library and verify every symbol and the ABI. The handle is
    /// freed when the library is unusable.
    let verify (library: string) : nativeint =
        let handle =
            try
                // a path is loaded as given; a bare name goes through the OS search (PATH, ld.so)
                if Path.IsPathRooted library || library.Contains(string Path.DirectorySeparatorChar)
                   || library.Contains("/") then
                    NativeLibrary.Load(Path.GetFullPath library)
                else
                    NativeLibrary.Load library
            with e ->
                raise (PolycallLoadException(library, "cannot load the library: " + e.Message, e))
        let missing =
            Native.symbols
            |> List.filter (fun s ->
                let mutable addr = 0n
                not (NativeLibrary.TryGetExport(handle, s, &addr)))
        if not missing.IsEmpty then
            NativeLibrary.Free handle
            raise (PolycallLoadException(library,
                    sprintf "missing symbol(s) [%s]: this is not a libpolycall >= 1.1.0 with binding ABI %d (an older 1.0 core?)"
                        (String.Join(", ", missing)) ExpectedAbi))
        let abiFn =
            Marshal.GetDelegateForFunctionPointer<Native.AbiVersionFn>(
                NativeLibrary.GetExport(handle, "polycall_ffi_abi_version"))
        let abi = abiFn.Invoke()
        if abi <> ExpectedAbi then
            NativeLibrary.Free handle
            raise (PolycallLoadException(library,
                    sprintf "polycall_ffi_abi_version() returned %d, this binding implements binding ABI %d"
                        abi ExpectedAbi))
        handle

    let private resolve () : string * nativeint =
        match Environment.GetEnvironmentVariable EnvVar with
        | explicit when not (String.IsNullOrWhiteSpace explicit) ->
            try explicit, verify explicit
            with :? PolycallLoadException as e ->
                raise (PolycallLoadException(explicit, e.Reason + " (selected by " + EnvVar + ")", e))
        | _ ->
            let names = defaultNames ()
            let rec attempt (remaining: string list) (last: exn) =
                match remaining with
                | [] ->
                    raise (PolycallLoadException(String.Join(", ", names),
                            "libpolycall was not found; set " + EnvVar
                            + " to the full path of the library or put it on the system library path", last))
                | name :: rest ->
                    try name, verify name
                    with :? PolycallLoadException as e when e.Reason.StartsWith "cannot load the library" ->
                        attempt rest e
            attempt names null

    let private state =
        lazy
            (try
                let name, handle = resolve ()
                NativeLibrary.SetDllImportResolver(
                    typeof<PolycallLoadException>.Assembly,
                    DllImportResolver(fun libName _ _ -> if libName = Native.Lib then handle else 0n))
                Ok(name, handle)
             with :? PolycallLoadException as e -> Error e)

    /// Make sure the library is loaded and verified; raise PolycallLoadException otherwise.
    let ensure () : string =
        match state.Force() with
        | Ok(name, _) -> name
        | Error e -> raise (PolycallLoadException(e.Library, e.Reason, e))
