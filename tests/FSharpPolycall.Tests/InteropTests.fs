module InteropTests

open System
open System.IO
open Expecto
open OBINexus.Polycall
open Fixtures

let private mixedPayload () =
    Array.append (bytes "héllo — 世界 \U0001F30D ") (Array.init 256 byte)

/// Split a command line on whitespace; double quotes group.
let splitCommand (command: string) =
    let parts = ResizeArray<string>()
    let cur = Text.StringBuilder()
    let mutable quoted = false
    let mutable any = false
    for c in command do
        if c = '"' then
            quoted <- not quoted
            any <- true
        elif Char.IsWhiteSpace c && not quoted then
            if any then
                parts.Add(cur.ToString())
                cur.Clear() |> ignore
                any <- false
        else
            cur.Append c |> ignore
            any <- true
    if any then parts.Add(cur.ToString())
    List.ofSeq parts

let tests =
    testSequenced <| testList "interop" [
        // F# node <-> `polycall peer serve` (C CLI process), verified at each receiver
        testCase "F# peer and C CLI peer exchange payloads both ways" <| fun () ->
            let cli = requireCli ()
            let dir = tempDir ()
            use cnode = startCliPeer dir "cnode"
            use f = Peer.Open("fsnode", "127.0.0.1:0", token)
            f.Register("cnode", cnode.Endpoint)
            f.Ping("cnode", T)

            let payload = mixedPayload ()
            f.Send("cnode", payload, "f2c-1", T)
            let r = run cli [ "peer"; "recv"; "--to"; cnode.Endpoint; "-t"; "5000" ] [] 20000
            Expect.equal r.Exit 0 r.Stderr
            Expect.equal (jsonString r.Out "from") (Some "fsnode") r.Out
            Expect.equal (jsonString r.Out "id") (Some "f2c-1") r.Out
            Expect.isTrue ((jsonString r.Out "payload_b64" |> Option.map Convert.FromBase64String) = Some payload) "bytes at the C node"

            let big = Array.zeroCreate<byte> Polycall.PeerMaxPayload
            Random(3).NextBytes big
            f.Send("cnode", big, "f2c-max", 20000u)
            let r = run cli [ "peer"; "recv"; "--to"; cnode.Endpoint; "-t"; "10000"; "--raw" ] [] 30000
            Expect.equal r.Exit 0 r.Stderr
            Expect.isTrue (r.Stdout = big) "1 MiB payload identical at the C node"

            let file = Path.Combine(dir, "c2f.bin")
            File.WriteAllBytes(file, payload)
            let r = run cli [ "peer"; "send"; "--from"; "cnode"; "--to"; f.Endpoint; "--id"; "c2f-1"; "--payload-file"; file ] [] 20000
            Expect.equal r.Exit 0 r.Stderr
            expectMessage (f.Recv T) "cnode" "c2f-1" payload

            let bigFile = Path.Combine(dir, "c2f-max.bin")
            let bigBack = Array.zeroCreate<byte> Polycall.PeerMaxPayload
            Random(4).NextBytes bigBack
            File.WriteAllBytes(bigFile, bigBack)
            let r = run cli [ "peer"; "send"; "--from"; "cnode"; "--to"; f.Endpoint; "--id"; "c2f-max"; "--payload-file"; bigFile; "-t"; "20000" ] [] 30000
            Expect.equal r.Exit 0 r.Stderr
            expectMessage (f.Recv 20000u) "cnode" "c2f-max" bigBack

            let r = run cli [ "peer"; "send"; "--from"; "cnode"; "--to"; f.Endpoint; "--id"; "c2f-2"; "--payload"; "hello from C" ] [] 20000
            Expect.equal r.Exit 0 r.Stderr
            expectMessage (f.Recv T) "cnode" "c2f-2" (bytes "hello from C")
            let r = run cli [ "peer"; "send"; "--from"; "cnode"; "--to"; f.Endpoint; "--id"; "c2f-2"; "--payload"; "hello from C" ] [] 20000
            Expect.equal r.Exit 0 r.Stderr
            Expect.isNone (f.TryRecv 400u) "duplicate from the C CLI delivered twice"

        testCase "C CLI manages an F#-hosted node over the wire" <| fun () ->
            let cli = requireCli ()
            use f = Peer.Open("fshost", "127.0.0.1:0", token)
            let r = run cli [ "peer"; "health"; "--to"; f.Endpoint ] [] 20000
            Expect.equal r.Exit 0 r.Stderr
            Expect.stringContains r.Out "\"node_id\":\"fshost\"" "health"
            let r = run cli [ "peer"; "register"; "--to"; f.Endpoint; "--id"; "remote1"; "--peer-endpoint"; "127.0.0.1:9" ] [] 20000
            Expect.equal r.Exit 0 r.Stderr
            Expect.stringContains (f.List()) "\"remote1\":\"127.0.0.1:9\"" "registered over the wire"
            let r = run cli [ "peer"; "peers"; "--to"; f.Endpoint ] [ "POLYCALL_DEV_TOKEN", None ] 20000
            Expect.equal r.Exit 7 ("registry without the token: " + r.Out + r.Stderr)
            let r = run cli [ "peer"; "send"; "--from"; "mallory"; "--to"; f.Endpoint; "--payload"; "x" ] [ "POLYCALL_DEV_TOKEN", Some "wrong-token" ] 20000
            Expect.equal r.Exit 7 ("wrong token: " + r.Out + r.Stderr)
            Expect.isNone (f.TryRecv 300u) "nothing from mallory may be queued"

        // F# node <-> another binding's echo agent (POLYCALL_INTEROP_ECHO), e.g. java-polycall:
        //   AGENT peer echo --node-id ID --endpoint 127.0.0.1:0 --endpoint-file F
        //         --peer ORIGIN=HOST:PORT --count N --idle-timeout-ms MS
        testCase "F# peer exchanges payloads with another binding's peer" <| fun () ->
            let command = Environment.GetEnvironmentVariable "POLYCALL_INTEROP_ECHO"
            if String.IsNullOrWhiteSpace command then
                skiptest "POLYCALL_INTEROP_ECHO not set (no other binding's echo agent provided)"
            let label = Environment.GetEnvironmentVariable "POLYCALL_INTEROP_ECHO_NAME" |> Option.ofObj |> Option.defaultValue "external"
            let large = Array.zeroCreate<byte> 200000
            Random(11).NextBytes large
            let max = Array.zeroCreate<byte> Polycall.PeerMaxPayload
            Random(12).NextBytes max
            let payloads =
                [| [||]; bytes ("hello from fsharp to " + label)
                   bytes "héllo — 世界 \U0001F30D"; Array.init 256 byte; large; max |]
            use origin = Peer.Open("fsharp-origin", "127.0.0.1:0", token)
            let exe, args =
                match splitCommand command with
                | exe :: args -> exe, args
                | [] -> failtest "empty POLYCALL_INTEROP_ECHO"
            let dir = tempDir ()
            use agent =
                start dir "echo-agent" exe
                    (args @ [ "peer"; "echo"; "--node-id"; "echo-agent"; "--endpoint"; "127.0.0.1:0"
                              "--peer"; "fsharp-origin=" + origin.Endpoint
                              "--count"; string payloads.Length; "--idle-timeout-ms"; "30000" ])
            origin.Register("echo-agent", agent.Endpoint)
            payloads |> Array.iteri (fun i p ->
                origin.Send("echo-agent", p, sprintf "x%d" i, 20000u)
                let m = origin.Recv 20000u
                Expect.equal m.Sender "echo-agent" agent.Log
                Expect.equal m.MessageId (sprintf "echo-x%d" i) "echo id"
                Expect.isTrue (m.Payload = p) (sprintf "payload %d after the round trip" i))
            Expect.isTrue (agent.Process.WaitForExit 20000) ("agent did not finish: " + agent.Log)
            Expect.equal agent.Process.ExitCode 0 agent.Log
            Expect.stringContains agent.Log "\"from\":\"fsharp-origin\"" "agent saw the F# sender"
            printfn "echo interop with %s: %d round trips verified" label payloads.Length
    ]
