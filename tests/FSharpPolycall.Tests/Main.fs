module Main

open Expecto

/// Every test runs against the REAL installed library (POLYCALL_LIBRARY or the
/// platform name) and the real polycall CLI (POLYCALL_CLI or PATH). Checks
/// that cannot run are reported as ignored (SKIP), never as passed.
[<EntryPoint>]
let main argv =
    let all =
        testSequenced <| testList "fsharp-polycall" [
            LibraryTests.tests
            ConfigTests.tests
            CallTests.tests
            PeerTests.tests
            InteropTests.tests
        ]
    try
        runTestsWithCLIArgs [] argv all
    finally
        CallTests.cleanup ()
