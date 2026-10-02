module LibraryTests

open System
open System.IO
open System.Threading
open Expecto
open OBINexus.Polycall
open Fixtures

let tests =
    testList "library" [
        testCase "abi version is 1" <| fun () ->
            Expect.equal (Polycall.abiVersion ()) 1 "polycall_ffi_abi_version"
            Expect.equal Polycall.AbiVersion 1 "binding constant"

        testCase "version is at least 1.1.0" <| fun () ->
            let v = Polycall.version ()
            let parts = v.Split('.')
            let major, minor = int parts.[0], int parts.[1]
            Expect.isTrue (major > 1 || (major = 1 && minor >= 1)) ("binding ABI 1 needs >= 1.1.0, got " + v)
            Expect.isNotEmpty (Polycall.coreVersion ()) "polycall_get_version (1.0 API)"

        testCase "POLYCALL_LIBRARY is honoured" <| fun () ->
            match Environment.GetEnvironmentVariable "POLYCALL_LIBRARY" with
            | env when String.IsNullOrWhiteSpace env -> skiptest "POLYCALL_LIBRARY is not set for this run"
            | env -> Expect.equal (Polycall.libraryName ()) env "loaded library"

        testCase "platform name is used without POLYCALL_LIBRARY" <| fun () ->
            match Environment.GetEnvironmentVariable "POLYCALL_LIBRARY" with
            | env when not (String.IsNullOrWhiteSpace env) ->
                skiptest "POLYCALL_LIBRARY is set (the default search is covered by a separate run)"
            | _ -> Expect.contains (Polycall.defaultLibraryNames ()) (Polycall.libraryName ()) "default name"

        testCase "strerror names every status" <| fun () ->
            for code in Enum.GetValues<PolycallStatus>() do
                let expected =
                    match code with
                    | PolycallStatus.Ok -> "POLYCALL_OK"
                    | _ ->
                        "POLYCALL_E_" + (Text.RegularExpressions.Regex.Replace(string code, "(?<=[a-z])([A-Z])", "_$1")).ToUpperInvariant()
                let s = Polycall.strerror (int code)
                Expect.stringStarts s (expected + ":") (sprintf "%d" (int code))
            Expect.stringStarts (Polycall.strerror -999) "POLYCALL_E_UNKNOWN" "unknown code"

        testCase "last error is per-thread detail of the last failure" <| fun () ->
            let dir = tempDir ()
            Expect.equal (Polycall.runConfig (Path.Combine(dir, "missing-polycallrc"))) (int PolycallStatus.NotFound) "status"
            let detail = Polycall.lastError ()
            Expect.isNotEmpty detail "detail after a failure"
            let mutable other = null
            let t = Thread(fun () -> other <- Polycall.lastError ())
            t.Start()
            t.Join()
            Expect.equal other "" "another thread has its own state"
            Expect.equal (Polycall.lastError ()) detail "unchanged on this thread"

        testCase "missing library is a clear error, not a crash" <| fun () ->
            let bogus = Path.Combine(tempDir (), (if isWindows then "no-such-polycall.dll" else "libno-such-polycall.so.1"))
            let e = Expect.throwsC (fun () -> Polycall.probeLibrary bogus) id
            match e with
            | :? PolycallLoadException as e -> Expect.stringContains e.Reason "cannot load the library" e.Message
            | e -> failtestf "unexpected %O" e

        testCase "library without binding ABI symbols is a clear error" <| fun () ->
            if OperatingSystem.IsMacOS() then skiptest "no libc.so.6 on macOS"
            let other = if isWindows then "kernel32.dll" else "libc.so.6"
            match Expect.throwsC (fun () -> Polycall.probeLibrary other) id with
            | :? PolycallLoadException as e ->
                Expect.stringContains e.Reason "missing symbol(s)" e.Message
                Expect.stringContains e.Reason "polycall_ffi_abi_version" e.Message
            | e -> failtestf "unexpected %O" e

        // ABI mismatch: a clearly-labelled FAKE library (tests/fixtures/fake_polycall_abi2.c,
        // built by the test scripts) exporting every symbol with polycall_ffi_abi_version() == 2.
        testCase "abi mismatch is refused" <| fun () ->
            match Environment.GetEnvironmentVariable "POLYCALL_TEST_FAKE_ABI2" with
            | fake when String.IsNullOrWhiteSpace fake -> skiptest "POLYCALL_TEST_FAKE_ABI2 not set (fake ABI-2 library not built)"
            | fake ->
                match Expect.throwsC (fun () -> Polycall.probeLibrary fake) id with
                | :? PolycallLoadException as e -> Expect.stringContains e.Reason "returned 2" e.Message
                | e -> failtestf "unexpected %O" e

        testCase "default names follow the platform contract" <| fun () ->
            let expected =
                if isWindows then [ "polycall.dll"; "libpolycall.dll" ]
                elif OperatingSystem.IsMacOS() then [ "libpolycall.1.dylib" ]
                else [ "libpolycall.so.1" ]
            Expect.equal (Polycall.defaultLibraryNames ()) expected "names"
    ]
