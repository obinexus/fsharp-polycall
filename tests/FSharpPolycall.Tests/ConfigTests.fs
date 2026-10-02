module ConfigTests

open System.IO
open Expecto
open OBINexus.Polycall
open Fixtures

let private write (name: string) (text: string) =
    let p = Path.Combine(tempDir (), name)
    File.WriteAllText(p, text)
    p

/// The repository root (tests run with the repo as working directory or from bin/).
let private repoFile (rel: string) =
    let rec up (d: DirectoryInfo) =
        if isNull d then None
        elif File.Exists(Path.Combine(d.FullName, "fsharp-polycallrc")) then Some(Path.Combine(d.FullName, rel))
        else up d.Parent
    match Option.ofObj (System.Environment.GetEnvironmentVariable "FSHARP_POLYCALL_REPO") with
    | Some r -> Some(Path.Combine(r, rel))
    | None ->
        match up (DirectoryInfo(Directory.GetCurrentDirectory())) with
        | Some p -> Some p
        | None -> up (DirectoryInfo(System.AppContext.BaseDirectory))

let tests =
    testList "run_config" [
        testCase "valid config passes strict and lenient" <| fun () ->
            let f = write "valid-polycallrc" "log_level=info\nmax_connections=10\nnetwork_timeout=500\ntls_enabled=false\n"
            Expect.equal (Polycall.runConfig f) 0 "strict status"
            Polycall.runConfigOrRaise f
            Polycall.validate f
            Expect.equal (Polycall.tryRunConfig f) (Ok()) "Result API"

        testCase "missing file is NotFound" <| fun () ->
            let f = Path.Combine(tempDir (), "absent-polycallrc")
            Expect.equal (Polycall.runConfig f) (int PolycallStatus.NotFound) "status"
            expectStatus PolycallStatus.NotFound (fun () -> Polycall.runConfigOrRaise f) |> ignore
            Expect.equal (Polycall.tryRunConfig f) (Error(int PolycallStatus.NotFound)) "Result API"

        testCase "invalid value is Config with detail" <| fun () ->
            let f = write "invalid-polycallrc" "max_connections=lots\n"
            let e = expectStatus PolycallStatus.Config (fun () -> Polycall.validate f)
            Expect.stringContains e.Detail "max_connections" "detail names the key"
            expectStatus PolycallStatus.Config (fun () -> Polycall.runConfigOrRaise f) |> ignore

        testCase "malformed syntax is Config" <| fun () ->
            let f = write "garbage-polycallrc" "this is not a config line\n"
            expectStatus PolycallStatus.Config (fun () -> Polycall.runConfigOrRaise f) |> ignore

        testCase "unknown key: warning lenient, error strict" <| fun () ->
            let f = write "unknown-polycallrc" "log_level=info\nbogus_key=1\n"
            Polycall.validate f
            let e = expectStatus PolycallStatus.Config (fun () -> Polycall.runConfigOrRaise f)
            Expect.stringContains e.Detail "bogus_key" "detail"

        testCase "tls_enabled=true refused when running strict" <| fun () ->
            let f = write "tls-polycallrc" "tls_enabled=true\ncert_file=/x/c.pem\nkey_file=/x/k.pem\n"
            Polycall.validate f
            let e = expectStatus PolycallStatus.Unsupported (fun () -> Polycall.runConfigOrRaise f)
            Expect.stringContains e.Detail "tls_enabled" "detail"

        testCase "empty, null and NUL paths are InvalidArgument" <| fun () ->
            Expect.equal (Polycall.runConfig "") (int PolycallStatus.InvalidArgument) "empty"
            expectStatus PolycallStatus.InvalidArgument (fun () -> Polycall.runConfig null) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> Polycall.runConfig "a\000b") |> ignore

        testCase "UTF-8 path is passed intact" <| fun () ->
            let f = write "café-世界-polycallrc" "log_level=info\n"
            Polycall.runConfigOrRaise f

        testCase "shipped configurations validate for running" <| fun () ->
            for rel in [ "fsharp-polycallrc"; Path.Combine("examples", "fsharp-polycallrc") ] do
                match repoFile rel with
                | Some p when File.Exists p -> Polycall.runConfigOrRaise p
                | _ -> skiptest "repository files not found (set FSHARP_POLYCALL_REPO)"

        testCase "describe returns JSON" <| fun () ->
            let f = write "describe-polycallrc" "log_level=debug\nmax_connections=7\n"
            let json = Polycall.describe f
            Expect.isTrue (json.StartsWith "{" && json.EndsWith "}") json
            Expect.stringContains json "max_connections" "keys"
            expectStatus PolycallStatus.NotFound (fun () -> Polycall.describe (Path.Combine(tempDir (), "nope"))) |> ignore
    ]
