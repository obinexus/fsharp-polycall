namespace OBINexus.Polycall

open System
open System.Runtime.InteropServices

module private Native =
    [<Literal>]
    let LibraryName = "fsharp_polycall"

    [<DllImport(LibraryName,
                EntryPoint = "fsharp_polycall_run_config",
                CallingConvention = CallingConvention.Cdecl)>]
    extern int runConfig(
        [<MarshalAs(UnmanagedType.LPUTF8Str)>] string configPath)

/// Raised when libpolycall returns a non-zero status.
[<Sealed>]
type PolycallException(status: int, configPath: string) =
    inherit Exception(
        $"libpolycall failed with status {status} for config '{configPath}'")

    member _.Status = status
    member _.ConfigPath = configPath

/// Thin F# adapter for libpolycall 1.5.
[<RequireQualifiedAccess>]
module Polycall =
    [<Literal>]
    let DefaultConfig = "fsharp-polycallrc"

    /// Run a configuration and return the unchanged libpolycall status.
    let runConfig (configPath: string) =
        if isNull configPath then
            nullArg "configPath"

        Native.runConfig configPath

    /// Run the default fsharp-polycallrc configuration.
    let runDefault () = runConfig DefaultConfig

    /// Convert the native status into an F# Result.
    let tryRunConfig configPath =
        match runConfig configPath with
        | 0 -> Ok ()
        | status -> Error status

    /// Run a configuration and raise PolycallException on failure.
    let runConfigOrRaise configPath =
        match runConfig configPath with
        | 0 -> ()
        | status -> raise (PolycallException(status, configPath))

    /// Run the default configuration and raise on failure.
    let runDefaultOrRaise () = runConfigOrRaise DefaultConfig
