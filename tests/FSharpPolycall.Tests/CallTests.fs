module CallTests

open System
open System.Diagnostics
open System.IO
open Expecto
open OBINexus.Polycall
open Fixtures

/// polycall_call against a real `polycall start` runtime (one per test list).
let private runtime = lazy (startRuntime (tempDir ()))
let private ep () = runtime.Force().Endpoint

/// Stop the runtime once all tests ran (called from Main).
let cleanup () =
    if runtime.IsValueCreated then (runtime.Value :> IDisposable).Dispose()

let tests =
    testSequenced <| testList "call" [
        testCase "success returns the operation output" <| fun () ->
            let out = Polycall.call (ep ()) "inventory" "get" "{\"item_id\":\"widget-a\"}" 5000u
            Expect.stringContains out "\"quantity\":42" out
            Expect.stringContains out "\"in_stock\":true" out

        testCase "UTF-8 input round-trips through debug.echo" <| fun () ->
            let input = "{\"text\":\"héllo — 世界 \U0001F30D\"}"
            Expect.equal (Polycall.call (ep ()) "debug" "echo" input 5000u) ("{\"echo\":" + input + "}") "echo"

        testCase "null input is sent as JSON null" <| fun () ->
            Expect.equal (Polycall.call (ep ()) "debug" "echo" null 5000u) "{\"echo\":null}" "echo"

        testCase "unknown operation is NotFound with the remote error object" <| fun () ->
            let e = expectStatus PolycallStatus.NotFound (fun () -> Polycall.call (ep ()) "inventory" "nope" "{}" 5000u)
            Expect.isNotNull e.RemoteError "remote error"
            Expect.stringContains e.RemoteError "operation.unknown" "code"

        testCase "operation error is Remote" <| fun () ->
            let e = expectStatus PolycallStatus.Remote (fun () -> Polycall.call (ep ()) "inventory" "get" "{\"item_id\":\"nope\"}" 5000u)
            Expect.stringContains e.RemoteError "item.unknown" "code"

        testCase "deadline exceeded is Timeout" <| fun () ->
            let sw = Stopwatch.StartNew()
            let e = expectStatus PolycallStatus.Timeout (fun () -> Polycall.call (ep ()) "debug" "sleep" "{\"ms\":5000}" 300u)
            Expect.isLessThan sw.ElapsedMilliseconds 4000L "returned promptly"
            Expect.isNotNull e.RemoteError "remote error"

        testCase "invalid input is rejected before any I/O" <| fun () ->
            expectStatus PolycallStatus.InvalidArgument (fun () -> Polycall.call (ep ()) "debug" "echo" "{not json" 5000u) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> Polycall.call (ep ()) "debug" "echo" "{}" 0u) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> Polycall.call (ep ()) "debug" "echo" "{}" 600001u) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> Polycall.call "no-port" "debug" "echo" "{}" 5000u) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> Polycall.call (ep ()) "" "echo" "{}" 5000u) |> ignore

        testCase "no runtime is Transport" <| fun () ->
            let port = freePort ()
            expectStatus PolycallStatus.Transport (fun () -> Polycall.call (sprintf "127.0.0.1:%d" port) "inventory" "get" "{}" 2000u) |> ignore

        testCase "concurrent calls from many threads" <| fun () ->
            let results =
                [| 0 .. 63 |]
                |> Array.Parallel.map (fun n -> n, Polycall.call (ep ()) "debug" "echo" (sprintf "{\"n\":%d}" n) 10000u)
            for n, out in results do
                Expect.equal out (sprintf "{\"echo\":{\"n\":%d}}" n) "echo"

        testCase "call through polycall daemon" <| fun () ->
            let cli = requireCli ()
            let project = tempDir ()
            let file = Path.Combine(project, "Polycallfile")
            File.WriteAllText(file, "# fsharp-polycall daemon test\nserver node 8080:8084\nnetwork start\ndaemon_endpoint=127.0.0.1:0\nauth_token_env=POLYCALL_DEV_TOKEN\n")
            let start = run cli [ "--format"; "json"; "daemon"; "start"; "-t"; "15000"; file ] [] 30000
            try
                Expect.equal start.Exit 0 (start.Out + start.Stderr)
                match jsonString start.Out "endpoint" with
                | Some endpoint ->
                    let out = Polycall.call endpoint "inventory" "get" "{\"item_id\":\"widget-b\"}" 5000u
                    Expect.stringContains out "\"quantity\":7" out
                | None -> failtestf "no endpoint in %s" start.Out
            finally
                let stop = run cli [ "daemon"; "stop"; "-t"; "10000"; file ] [] 30000
                Expect.equal stop.Exit 0 ("daemon stop: " + stop.Out + stop.Stderr)
    ]
