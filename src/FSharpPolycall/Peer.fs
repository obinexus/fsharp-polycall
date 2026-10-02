namespace OBINexus.Polycall

open System
open System.Runtime.InteropServices
open System.Text

/// One message taken from a peer node's inbox.
type PeerMessage =
    { /// The sending node's id.
      Sender: string
      /// The message id (de-duplication key together with the sender).
      MessageId: string
      /// The exact payload bytes (binary-safe).
      Payload: byte[] }
    /// The payload decoded as UTF-8.
    member m.Text = Encoding.UTF8.GetString m.Payload

/// Owns one polycall_peer_t. SafeHandle gives idempotent, critical-finalizer
/// disposal; the raw int handle (not the SafeHandle) is passed to P/Invoke so
/// Dispose can close the node while another thread is blocked in recv,
/// which the library wakes with POLYCALL_E_CLOSED.
[<Sealed>]
type PeerHandle(handle: int) =
    inherit SafeHandle(nativeint handle, true)

    member val CloseStatus = 0 with get, set
    member _.Value = handle
    override this.IsInvalid = this.DangerousGetHandle() <= 0n

    override this.ReleaseHandle() =
        this.CloseStatus <- Native.polycall_peer_close handle
        this.CloseStatus = 0

/// A Polycall peer node (polycall_peer_*): its own registry and inbox,
/// exchanging payloads directly with other nodes in any language/process.
/// Thread-safe. Dispose/Close are idempotent; methods of a closed peer pass
/// the stale handle to the library, which reports PolycallStatus.InvalidHandle.
[<Sealed; AllowNullLiteral>]
type Peer private (handle: PeerHandle) =
    static let initialRecvCapacity = 64 * 1024

    let h = handle.Value

    /// UINT32_MAX: block in Recv until a message, Cancel or Close.
    static member WaitForever = UInt32.MaxValue

    /// Open a node (polycall_peer_open). bindEndpoint "127.0.0.1:0" = ephemeral
    /// port, null = send-only; authToken null/"" = no authentication.
    static member Open(nodeId: string, bindEndpoint: string, authToken: string) : Peer =
        Loader.ensure () |> ignore
        let id = Interop.cstr "nodeId" nodeId
        let ep = Interop.cstrOpt "bindEndpoint" bindEndpoint
        let tok = Interop.cstrOpt "authToken" authToken
        let mutable out = 0
        let st = Native.polycall_peer_open(id, ep, tok, &out)
        if st <> 0 then Interop.fail st (sprintf "peer_open(%s, %s)" nodeId bindEndpoint)
        new Peer(new PeerHandle(out))

    /// Open a listening node without authentication.
    static member Open(nodeId: string, bindEndpoint: string) = Peer.Open(nodeId, bindEndpoint, null)

    /// Close a raw handle (polycall_peer_close). The library validates it: an
    /// unknown, closed or stale handle raises PolycallStatus.InvalidHandle.
    static member CloseHandle(rawHandle: int) =
        Loader.ensure () |> ignore
        Interop.check (Native.polycall_peer_close rawHandle) (sprintf "peer_close(%d)" rawHandle)

    /// The library's integer handle (> 0).
    member _.Handle = h

    /// True once this object was closed/disposed.
    member _.IsClosed = handle.IsClosed

    /// The bound "host:port" ("" for a send-only node).
    member _.Endpoint =
        Interop.textCall "peer_endpoint" (fun buf cap ->
            let st = Native.polycall_peer_endpoint(h, buf, cap)
            st, 0un)

    /// This node's id.
    member _.NodeId =
        Interop.textCall "peer_node_id" (fun buf cap ->
            let st = Native.polycall_peer_node_id(h, buf, cap)
            st, 0un)

    /// Add or replace peerId -> endpoint in THIS node's registry.
    member _.Register(peerId: string, endpoint: string) =
        let st = Native.polycall_peer_register(h, Interop.cstr "peerId" peerId, Interop.cstr "endpoint" endpoint)
        Interop.check st (sprintf "peer_register(%s)" peerId)

    /// Remove peerId; PolycallStatus.NotFound when it is not registered.
    member _.Unregister(peerId: string) =
        Interop.check (Native.polycall_peer_unregister(h, Interop.cstr "peerId" peerId))
                      (sprintf "peer_unregister(%s)" peerId)

    /// THIS node's registry as JSON {"id":"host:port",...}.
    member _.List() =
        Interop.textCall "peer_list" (fun buf cap ->
            let mutable n = 0un
            let st = Native.polycall_peer_list(h, buf, cap, &n)
            st, n)

    /// This node's health as JSON.
    member _.Health() =
        Interop.textCall "peer_health" (fun buf cap ->
            let mutable n = 0un
            let st = Native.polycall_peer_health(h, buf, cap, &n)
            st, n)

    /// GET /health of peer (registered id or "host:port"); OK only when it is
    /// healthy and, for a registered id, answers under that id.
    member _.Ping(peer: string, timeoutMs: uint32) =
        Interop.check (Native.polycall_peer_ping(h, Interop.cstr "peer" peer, timeoutMs))
                      (sprintf "peer_ping(%s)" peer)

    /// Deliver payload (<= 1 MiB, binary-safe) to peer. Exactly one attempt;
    /// returning means the receiver stored and acknowledged it. messageId
    /// null = generated. On Timeout retry with the SAME id.
    member _.Send(peer: string, payload: byte[], messageId: string, timeoutMs: uint32) =
        if isNull payload then raise (Interop.argumentError "payload is null")
        let data = if payload.Length = 0 then null else payload
        let st = Native.polycall_peer_send(h, Interop.cstr "peer" peer, data, unativeint payload.Length,
                                           Interop.cstrOpt "messageId" messageId, timeoutMs)
        Interop.check st (sprintf "peer_send(%s, %d bytes, id=%s)" peer payload.Length messageId)

    /// Send UTF-8 text.
    member this.SendText(peer: string, text: string, messageId: string, timeoutMs: uint32) =
        if isNull text then raise (Interop.argumentError "text is null")
        this.Send(peer, Encoding.UTF8.GetBytes text, messageId, timeoutMs)

    /// Take the oldest message into a payload buffer of exactly payloadCapacity
    /// bytes. A larger message raises TooLarge with RequiredSize and stays queued.
    member _.RecvInto(timeoutMs: uint32, payloadCapacity: int) : PeerMessage =
        if payloadCapacity < 0 then raise (Interop.argumentError "payloadCapacity < 0")
        let sender = Array.zeroCreate<byte> 64
        let mid = Array.zeroCreate<byte> 64
        let payload = if payloadCapacity = 0 then null else Array.zeroCreate<byte> payloadCapacity
        let mutable n = 0un
        let st = Native.polycall_peer_recv(h, timeoutMs, sender, 64un, mid, 64un,
                                           payload, unativeint payloadCapacity, &n)
        if st = int PolycallStatus.TooLarge then
            raise (Interop.error st "peer_recv" null (int64 n))
        Interop.check st "peer_recv"
        let len = int n
        { Sender = Interop.text sender
          MessageId = Interop.text mid
          Payload = if len = 0 then Array.empty else Array.sub payload 0 len }

    /// Take the oldest message, waiting up to timeoutMs (0 = poll,
    /// Peer.WaitForever = until a message, Cancel or Close). The buffer grows
    /// to whatever the message needs.
    member this.Recv(timeoutMs: uint32) : PeerMessage =
        let started = Diagnostics.Stopwatch.StartNew()
        let rec go (capacity: int) (wait: uint32) =
            let retry =
                try Choice1Of2(this.RecvInto(wait, capacity))
                with :? PolycallException as e when e.Status = int PolycallStatus.TooLarge
                                                    && e.RequiredSize > int64 capacity ->
                    Choice2Of2(int e.RequiredSize)
            match retry with
            | Choice1Of2 m -> m
            | Choice2Of2 needed ->
                // the message stayed queued: retry at once with a big enough buffer
                let remaining =
                    if timeoutMs = Peer.WaitForever then timeoutMs
                    else uint32 (max 0L (int64 timeoutMs - started.ElapsedMilliseconds))
                go needed remaining
        go initialRecvCapacity timeoutMs

    /// Like Recv, but None instead of raising Timeout.
    member this.TryRecv(timeoutMs: uint32) : PeerMessage option =
        try Some(this.Recv timeoutMs)
        with :? PolycallException as e when e.Status = int PolycallStatus.Timeout -> None

    /// Wake every Recv blocked on this node with PolycallStatus.Cancelled.
    member _.Cancel() = Interop.check (Native.polycall_peer_cancel h) "peer_cancel"

    /// Stop the listener, wake blocked receivers (Closed) and release the node. Idempotent.
    member _.Close() =
        if not handle.IsClosed then
            handle.Dispose()
            if handle.CloseStatus <> 0 then Interop.fail handle.CloseStatus (sprintf "peer_close(%d)" h)

    interface IDisposable with
        member this.Dispose() = this.Close()

    override _.ToString() =
        sprintf "Peer(handle=%d%s)" h (if handle.IsClosed then ", closed" else "")
