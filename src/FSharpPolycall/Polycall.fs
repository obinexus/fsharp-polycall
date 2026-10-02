namespace OBINexus.Polycall

open System
open System.Text

/// Binding ABI v1 status codes (polycall.h). Append-only.
type PolycallStatus =
    | Ok = 0
    | InvalidArgument = -1
    | NoMemory = -2
    | InvalidHandle = -3
    | Timeout = -4
    | Transport = -5
    | Protocol = -6
    | NotFound = -7
    | Auth = -8
    | Remote = -9
    | TooLarge = -10
    | Busy = -11
    | Cancelled = -12
    | Config = -13
    | AddressInUse = -14
    | Unsupported = -15
    | Permission = -16
    | Closed = -17
    | Internal = -18

module internal StatusText =
    /// "POLYCALL_E_TIMEOUT: deadline exceeded" -> "POLYCALL_E_TIMEOUT"
    let name (statusText: string) =
        match statusText.IndexOf ':' with
        | i when i > 0 -> statusText.Substring(0, i)
        | _ -> statusText

    let message (status: int) (statusText: string) (detail: string) (context: string) =
        let ctx = if String.IsNullOrEmpty context then "" else context + ": "
        let det = if String.IsNullOrEmpty detail then "" else ": " + detail
        sprintf "%s%s (%d)%s" ctx (name statusText) status det

/// A Polycall call returned a negative status: the code, its name from
/// polycall_strerror() and the calling thread's detail from polycall_last_error().
type PolycallException(status: int, statusText: string, detail: string, context: string,
                       remoteError: string, requiredSize: int64) =
    inherit Exception(StatusText.message status statusText detail context)

    /// The negative POLYCALL_E_* code.
    member _.Status = status
    /// The code as an enum.
    member _.StatusCode : PolycallStatus = enum status
    /// The status name, e.g. "POLYCALL_E_TIMEOUT".
    member _.StatusName = StatusText.name statusText
    /// The full polycall_strerror() text.
    member _.StatusText = statusText
    /// polycall_last_error() detail (may be empty).
    member _.Detail : string = if isNull detail then "" else detail
    /// For Polycall.call: the remote error object {"code":..,"message":..}, else null.
    member _.RemoteError = remoteError
    /// For PolycallStatus.TooLarge from a caller buffer: the size needed, else -1.
    member _.RequiredSize = requiredSize

/// Helpers shared by the API (UTF-8 marshalling and error construction).
module internal Interop =
    let utf8 = UTF8Encoding(false, true)

    /// NUL-terminated UTF-8 bytes of s (null stays null).
    let cstrOrNull (name: string) (s: string) : byte[] =
        if isNull s then null
        elif s.IndexOf '\000' >= 0 then
            invalidArg name (name + " contains a NUL character")
        else
            let n = utf8.GetByteCount s
            let b = Array.zeroCreate<byte> (n + 1)
            utf8.GetBytes(s, 0, s.Length, b, 0) |> ignore
            b

    /// Text up to the first NUL of a library-filled buffer.
    let text (buf: byte[]) : string =
        let n = Array.IndexOf(buf, 0uy)
        let n = if n < 0 then buf.Length else n
        Encoding.UTF8.GetString(buf, 0, n)

    let strerror (status: int) : string =
        let p = Native.polycall_strerror status
        if p = 0n then "" else Runtime.InteropServices.Marshal.PtrToStringUTF8 p

    let lastError () : string =
        let rec go (cap: int) =
            let buf = Array.zeroCreate<byte> cap
            let n = Native.polycall_last_error(buf, unativeint cap)
            if n < cap then text buf else go (n + 1)
        go 1024

    /// Build the exception for a failed call (reads polycall_last_error first, same thread).
    let error (status: int) (context: string) (remote: string) (required: int64) =
        let detail = lastError ()
        PolycallException(status, strerror status, detail, context, remote, required)

    let fail status context = raise (error status context null -1L)

    let check (status: int) (context: string) =
        if status <> 0 then fail status context

    /// A binding-side argument error, reported in the library's vocabulary.
    let argumentError (detail: string) =
        PolycallException(int PolycallStatus.InvalidArgument,
                          strerror (int PolycallStatus.InvalidArgument), detail, "binding", null, -1L)

    let cstr (name: string) (s: string) : byte[] =
        if isNull s then raise (argumentError (name + " is null"))
        try cstrOrNull name s
        with :? ArgumentException -> raise (argumentError (name + " contains a NUL character"))

    let cstrOpt (name: string) (s: string) : byte[] =
        try cstrOrNull name s
        with :? ArgumentException -> raise (argumentError (name + " contains a NUL character"))

    /// A text-returning call with an out_len (snprintf rules): grow and retry.
    let textCall (context: string) (call: byte[] -> unativeint -> int * unativeint) : string =
        let rec go (cap: int) =
            let buf = Array.zeroCreate<byte> cap
            let st, need = call buf (unativeint cap)
            if st = 0 then text buf
            elif st = int PolycallStatus.TooLarge && need >= unativeint cap then go (int need + 1)
            else fail st context
        go 4096

/// The Polycall API (Binding ABI v1). Every function is thread-safe.
[<RequireQualifiedAccess>]
module Polycall =
    /// The binding ABI this binding implements; the library must report the same.
    [<Literal>]
    let AbiVersion = 1

    /// The binding's default configuration file.
    [<Literal>]
    let DefaultConfig = "fsharp-polycallrc"

    /// POLYCALL_PEER_MAX_PAYLOAD (1 MiB).
    [<Literal>]
    let PeerMaxPayload = 1048576

    /// POLYCALL_CALL_MAX_OUTPUT (1 MiB).
    [<Literal>]
    let CallMaxOutput = 1048576

    /// The library name or path that was loaded (loads it on first use).
    let libraryName () = Loader.ensure ()

    /// polycall_ffi_abi_version() of the loaded library (verified == 1 at load).
    let abiVersion () =
        Loader.ensure () |> ignore
        Native.polycall_ffi_abi_version ()

    /// polycall_ffi_version(): e.g. "1.1.0".
    let version () =
        Loader.ensure () |> ignore
        let rec go cap =
            let buf = Array.zeroCreate<byte> cap
            let n = Native.polycall_ffi_version(buf, cap)
            if n < 0 then Interop.fail n "polycall_ffi_version"
            if n < cap then Interop.text buf else go (n + 1)
        go 32

    /// polycall_get_version() (the 1.0 API, kept by the library).
    let coreVersion () =
        Loader.ensure () |> ignore
        let p = Native.polycall_get_version ()
        if p = 0n then "" else Runtime.InteropServices.Marshal.PtrToStringUTF8 p

    /// polycall_strerror(status): static, never-null name and description.
    let strerror (status: int) =
        Loader.ensure () |> ignore
        Interop.strerror status

    /// polycall_last_error(): this thread's detail for its most recent failed call.
    let lastError () =
        Loader.ensure () |> ignore
        Interop.lastError ()

    /// polycall_ffi_run_config(path, strict ? 1 : 0), returning the unchanged status.
    let runConfigWith (strict: bool) (configPath: string) : int =
        Loader.ensure () |> ignore
        Native.polycall_ffi_run_config(Interop.cstr "configPath" configPath, (if strict then 1 else 0))

    /// The binding's documented entry point: polycall_ffi_run_config(path, 1),
    /// returning the unchanged status (0 = valid for running with this build).
    let runConfig (configPath: string) : int = runConfigWith true configPath

    /// Run the default fsharp-polycallrc configuration.
    let runDefault () = runConfig DefaultConfig

    /// polycall_ffi_run_config(path, 1) as an F# Result.
    let tryRunConfig (configPath: string) : Result<unit, int> =
        match runConfig configPath with
        | 0 -> Result.Ok()
        | status -> Result.Error status

    /// polycall_ffi_run_config(path, 1); raise PolycallException (name + detail) on failure.
    let runConfigOrRaise (configPath: string) : unit =
        let st = runConfig configPath
        if st <> 0 then Interop.fail st (sprintf "run_config(%s)" configPath)

    /// Run the default configuration and raise on failure.
    let runDefaultOrRaise () = runConfigOrRaise DefaultConfig

    /// polycall_ffi_run_config(path, 0): validate only (unknown keys are warnings).
    let validate (configPath: string) : unit =
        let st = runConfigWith false configPath
        if st <> 0 then Interop.fail st (sprintf "validate(%s)" configPath)

    /// polycall_ffi_describe(): JSON description of a configuration file.
    let describe (configPath: string) : string =
        Loader.ensure () |> ignore
        let path = Interop.cstr "configPath" configPath
        let rec go cap =
            let buf = Array.zeroCreate<byte> cap
            let n = Native.polycall_ffi_describe(path, buf, cap)
            if n < 0 then Interop.fail n (sprintf "describe(%s)" configPath)
            if n < cap then Interop.text buf else go (n + 1)
        go 65536

    /// One polycall_rpc v1 round trip (polycall_call) to a running runtime.
    /// inputJson may be null (sent as null); timeoutMs must be 1..600000.
    /// Raises PolycallException; RemoteError holds the runtime's error object.
    let call (endpoint: string) (service: string) (operation: string)
             (inputJson: string) (timeoutMs: uint32) : string =
        Loader.ensure () |> ignore
        let ep = Interop.cstr "endpoint" endpoint
        let svc = Interop.cstr "service" service
        let op = Interop.cstr "operation" operation
        let input = Interop.cstrOpt "inputJson" inputJson
        let cap = CallMaxOutput + 1
        let out = Array.zeroCreate<byte> cap
        let mutable outLen = 0un
        let st = Native.polycall_call(ep, svc, op, input, timeoutMs, out, unativeint cap, &outLen)
        if st = 0 then
            Interop.text out
        else
            let ctx = sprintf "%s.%s at %s" service operation endpoint
            if st = int PolycallStatus.TooLarge then
                raise (Interop.error st ctx null (int64 outLen))
            elif outLen > 0un then
                raise (Interop.error st ctx (Interop.text out) -1L)
            else
                raise (Interop.error st ctx null -1L)

    /// Verify a specific library (path or bare name) without making it the
    /// process-wide one: missing library, missing symbols and ABI mismatch
    /// raise PolycallLoadException. Used by tests and diagnostics.
    let probeLibrary (library: string) : unit =
        let h = Loader.verify library
        Runtime.InteropServices.NativeLibrary.Free h

    /// Library names tried when POLYCALL_LIBRARY is not set.
    let defaultLibraryNames () = Loader.defaultNames ()
