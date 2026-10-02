module PeerTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open OBINexus.Polycall
open Fixtures

/// Two real nodes (alpha, beta) in this process, registered with each other.
let private withPair (f: Peer -> Peer -> unit) () =
    use alpha = Peer.Open("alpha", "127.0.0.1:0", token)
    use beta = Peer.Open("beta", "127.0.0.1:0", token)
    alpha.Register("beta", beta.Endpoint)
    beta.Register("alpha", alpha.Endpoint)
    f alpha beta

let private randomBytes (seed: int) (n: int) =
    let b = Array.zeroCreate<byte> n
    Random(seed).NextBytes b
    b

/// Open a node and drop every reference to it (not inlined, so the JIT
/// cannot keep the object alive in the caller's frame).
[<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let private openAndForget () : string =
    let p = Peer.Open("leaked", "127.0.0.1:0", token)
    p.Endpoint

let tests =
    testSequenced <| testList "peer" [
        testCase "node id and endpoint" <| withPair (fun a b ->
            Expect.equal a.NodeId "alpha" "node id"
            Expect.isMatch a.Endpoint @"^127\.0\.0\.1:\d+$" "endpoint"
            Expect.notEqual a.Endpoint b.Endpoint "distinct endpoints"
            Expect.isGreaterThan a.Handle 0 "handle > 0")

        testCase "payloads in both directions verified at the receiver" <| withPair (fun a b ->
            a.SendText("beta", "hello beta", "m-a2b", T)
            expectMessage (b.Recv T) "alpha" "m-a2b" (bytes "hello beta")
            b.SendText("alpha", "hello alpha", "m-b2a", T)
            expectMessage (a.Recv T) "beta" "m-b2a" (bytes "hello alpha"))

        testCase "empty payload" <| withPair (fun a b ->
            a.Send("beta", [||], "m-empty", T)
            expectMessage (b.Recv T) "alpha" "m-empty" [||])

        testCase "UTF-8 payload" <| withPair (fun a b ->
            let text = "héllo — 世界 \U0001F30D"
            a.SendText("beta", text, "m-utf8", T)
            let m = b.Recv T
            expectMessage m "alpha" "m-utf8" (bytes text)
            Expect.equal m.Text text "decoded text")

        testCase "binary payload with NUL bytes" <| withPair (fun a b ->
            let all = Array.init 256 byte
            b.Send("alpha", all, "m-bin", T)
            expectMessage (a.Recv T) "beta" "m-bin" all)

        testCase "exactly 1 MiB" <| withPair (fun a b ->
            let big = randomBytes 42 Polycall.PeerMaxPayload
            a.Send("beta", big, "m-max", 20000u)
            expectMessage (b.Recv 20000u) "alpha" "m-max" big)

        testCase "1 MiB + 1 is refused before any I/O" <| withPair (fun a b ->
            let over = Array.zeroCreate<byte> (Polycall.PeerMaxPayload + 1)
            expectStatus PolycallStatus.TooLarge (fun () -> a.Send("beta", over, "m-over", T)) |> ignore
            Expect.isNone (b.TryRecv 300u) "nothing may arrive"
            Expect.stringContains (b.Health()) "\"received\":0" "receiver counted nothing")

        testCase "registry belongs to each node and changes only explicitly" <| fun () ->
            use c = Peer.Open("carol", "127.0.0.1:0", token)
            use d = Peer.Open("dave", "127.0.0.1:0", token)
            Expect.equal (c.List()) "{}" "carol starts empty"
            c.Register("dave", d.Endpoint)
            Expect.equal (c.List()) (sprintf "{\"dave\":\"%s\"}" d.Endpoint) "carol lists dave"
            Expect.equal (d.List()) "{}" "registering on carol must not touch dave"
            c.SendText("dave", "hi", "m-reg", T)
            expectMessage (d.Recv T) "carol" "m-reg" (bytes "hi")
            Expect.equal (d.List()) "{}" "receiving never registers the sender"
            expectStatus PolycallStatus.NotFound (fun () -> d.SendText("carol", "back", "m-x", T)) |> ignore
            c.Unregister "dave"
            Expect.equal (c.List()) "{}" "unregistered"
            expectStatus PolycallStatus.NotFound (fun () -> c.Unregister "dave") |> ignore
            expectStatus PolycallStatus.NotFound (fun () -> c.SendText("dave", "x", "m-y", T)) |> ignore
            c.SendText(d.Endpoint, "direct", "m-direct", T)
            expectMessage (d.Recv T) "carol" "m-direct" (bytes "direct")

        testCase "duplicate message id is delivered once" <| withPair (fun a b ->
            a.SendText("beta", "once", "m-dup", T)
            a.SendText("beta", "once", "m-dup", T)
            expectMessage (b.Recv T) "alpha" "m-dup" (bytes "once")
            Expect.isNone (b.TryRecv 500u) "duplicate must not be queued"
            Expect.stringContains (b.Health()) "\"duplicates\":1" "counted as duplicate")

        testCase "wrong or missing token is an Auth failure" <| withPair (fun _ b ->
            use mallory = Peer.Open("mallory", null, "wrong-token")
            use anon = Peer.Open("anon", null, null)
            expectStatus PolycallStatus.Auth (fun () -> mallory.SendText(b.Endpoint, "x", "m-auth1", T)) |> ignore
            expectStatus PolycallStatus.Auth (fun () -> anon.SendText(b.Endpoint, "x", "m-auth2", T)) |> ignore
            Expect.isNone (b.TryRecv 300u) "nothing from an unauthenticated sender"
            anon.Ping(b.Endpoint, T))

        testCase "send to a dead peer is Transport" <| withPair (fun a _ ->
            let dead = Peer.Open("dead", "127.0.0.1:0", token)
            let ep = dead.Endpoint
            dead.Close()
            expectStatus PolycallStatus.Transport (fun () -> a.SendText(ep, "into the void", "m-dead", 3000u)) |> ignore
            expectStatus PolycallStatus.Transport (fun () -> a.Ping(ep, 3000u)) |> ignore)

        testCase "receive timeout" <| withPair (fun _ b ->
            let sw = Stopwatch.StartNew()
            expectStatus PolycallStatus.Timeout (fun () -> b.Recv 250u) |> ignore
            Expect.isTrue (sw.ElapsedMilliseconds >= 200L && sw.ElapsedMilliseconds < 5000L)
                (sprintf "waited %d ms" sw.ElapsedMilliseconds)
            expectStatus PolycallStatus.Timeout (fun () -> b.Recv 0u) |> ignore
            Expect.isNone (b.TryRecv 0u) "poll")

        testCase "too-small buffer leaves the message queued" <| withPair (fun a b ->
            let hundred = randomBytes 7 100
            a.Send("beta", hundred, "m-small", T)
            let e = expectStatus PolycallStatus.TooLarge (fun () -> b.RecvInto(T, 10))
            Expect.equal e.RequiredSize 100L "required size"
            let e = expectStatus PolycallStatus.TooLarge (fun () -> b.RecvInto(T, 0))
            Expect.equal e.RequiredSize 100L "required size (no buffer)"
            expectMessage (b.RecvInto(T, 100)) "alpha" "m-small" hundred
            let large = randomBytes 8 300000
            a.Send("beta", large, "m-large", T)
            expectMessage (b.Recv T) "alpha" "m-large" large)

        testCase "cancel wakes a blocked receive" <| withPair (fun a b ->
            let waiter = Task.Run(fun () -> try Choice1Of2(b.Recv Peer.WaitForever) with e -> Choice2Of2 e)
            Thread.Sleep 400
            b.Cancel()
            Expect.isTrue (waiter.Wait 5000) "cancel must wake the receiver"
            match waiter.Result with
            | Choice2Of2(:? PolycallException as e) -> Expect.equal e.StatusCode PolycallStatus.Cancelled e.Message
            | other -> failtestf "unexpected %A" other
            a.SendText("beta", "after cancel", "m-after", T)
            expectMessage (b.Recv T) "alpha" "m-after" (bytes "after cancel"))

        testCase "close wakes a blocked receive" <| fun () ->
            let c = Peer.Open("closer", "127.0.0.1:0", token)
            let waiter = Task.Run(fun () -> try Choice1Of2(c.Recv Peer.WaitForever) with e -> Choice2Of2 e)
            Thread.Sleep 400
            c.Close()
            Expect.isTrue (waiter.Wait 5000) "close must wake the receiver"
            match waiter.Result with
            | Choice2Of2(:? PolycallException as e) -> Expect.equal e.StatusCode PolycallStatus.Closed e.Message
            | other -> failtestf "unexpected %A" other

        testCase "double close, calls after close and invalid handles" <| withPair (fun a b ->
            let c = Peer.Open("gone", "127.0.0.1:0", token)
            let h = c.Handle
            c.Close()
            Expect.isTrue c.IsClosed "closed"
            c.Close()
            (c :> IDisposable).Dispose()
            expectStatus PolycallStatus.InvalidHandle (fun () -> Peer.CloseHandle h) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.Endpoint) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.NodeId) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.List()) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.Health()) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.Cancel()) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.Register("x", "127.0.0.1:1")) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.Unregister "x") |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.Ping(b.Endpoint, T)) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.SendText(b.Endpoint, "x", "m-closed", T)) |> ignore
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.Recv 0u) |> ignore
            for bogus in [ 0; -1; Int32.MaxValue; Int32.MinValue; h + 1 ] do
                if bogus <> a.Handle && bogus <> b.Handle then
                    expectStatus PolycallStatus.InvalidHandle (fun () -> Peer.CloseHandle bogus) |> ignore
            use e = Peer.Open("fresh", "127.0.0.1:0", token)
            Expect.notEqual e.Handle h "a stale handle is never reused"
            expectStatus PolycallStatus.InvalidHandle (fun () -> c.Endpoint) |> ignore
            Expect.equal e.NodeId "fresh" "new node works")

        testCase "concurrent senders" <| withPair (fun _ b ->
            let senders = [ for s in 0 .. 3 -> Peer.Open(sprintf "sender-%d" s, null, token) ]
            try
                for p in senders do
                    p.Register("beta", b.Endpoint)
                let work =
                    [| for s in 0 .. 3 do
                         for t in 0 .. 1 ->
                             Task.Run(fun () ->
                                 for i in 0 .. 19 do
                                     let id = sprintf "s%d-t%d-%d" s t i
                                     senders.[s].SendText("beta", id, id, 10000u)) |]
                Expect.isTrue (Task.WaitAll(work, 60000)) "senders finished"
                let seen = ConcurrentDictionary<string, bool>()
                for _ in 1 .. 160 do
                    let m = b.Recv T
                    Expect.equal m.Text m.MessageId "payload matches its id"
                    Expect.isTrue (seen.TryAdd(m.Sender + "/" + m.MessageId, true)) (sprintf "duplicate %A" m)
                Expect.equal seen.Count 160 "all messages exactly once"
                Expect.isNone (b.TryRecv 200u) "nothing extra"
            finally
                for p in senders do
                    p.Close())

        testCase "ping checks health and identity" <| withPair (fun a b ->
            a.Ping("beta", T)
            a.Ping(b.Endpoint, T)
            a.Register("gamma", b.Endpoint)
            let e = expectStatus PolycallStatus.Protocol (fun () -> a.Ping("gamma", T))
            Expect.stringContains e.Detail "beta" "detail names the real id"
            let health = b.Health()
            Expect.stringContains health "\"node_id\":\"beta\"" "health"
            Expect.stringContains health "\"protocol\":\"polycall-peer/1\"" "protocol")

        testCase "send-only node has no endpoint" <| withPair (fun _ b ->
            use s = Peer.Open("send-only", null, token)
            Expect.equal s.Endpoint "" "no listener"
            s.SendText(b.Endpoint, "from a send-only node", "m-so", T)
            expectMessage (b.Recv T) "send-only" "m-so" (bytes "from a send-only node"))

        testCase "invalid arguments and bind rules" <| withPair (fun a b ->
            expectStatus PolycallStatus.InvalidArgument (fun () -> Peer.Open("bad id!", "127.0.0.1:0", token)) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> Peer.Open(String('x', 64), "127.0.0.1:0", token)) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> Peer.Open("ok", "not-an-endpoint", token)) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> a.SendText("beta", "x", "bad id!", T)) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> a.SendText("", "x", "m-1", T)) |> ignore
            expectStatus PolycallStatus.InvalidArgument (fun () -> a.Register("bad id!", b.Endpoint)) |> ignore
            expectStatus PolycallStatus.Config (fun () -> Peer.Open("exposed", "0.0.0.0:0", null)) |> ignore
            expectStatus PolycallStatus.AddressInUse (fun () -> Peer.Open("clash", b.Endpoint, token)) |> ignore)

        testCase "error carries status, name and detail" <| withPair (fun a _ ->
            let e = expectStatus PolycallStatus.NotFound (fun () -> a.SendText("nobody", "x", "m-1", T))
            Expect.equal e.StatusName "POLYCALL_E_NOT_FOUND" "name"
            Expect.stringStarts e.StatusText "POLYCALL_E_NOT_FOUND:" "strerror text"
            Expect.stringContains e.Detail "nobody" "detail"
            Expect.stringContains e.Message "POLYCALL_E_NOT_FOUND (-7)" "message")

        testCase "finalizer-safe disposal: an undisposed peer is released by its SafeHandle" <| fun () ->
            let ep = openAndForget ()
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()
            // the listener is gone once the SafeHandle finalizer closed the node
            use probe = Peer.Open("probe", null, token)
            expectStatus PolycallStatus.Transport (fun () -> probe.Ping(ep, 2000u)) |> ignore
    ]
