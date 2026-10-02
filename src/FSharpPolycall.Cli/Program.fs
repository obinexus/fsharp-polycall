/// fsharp-polycall command line: a thin front end over the binding.
///
///   fsharp-polycall version
///   fsharp-polycall validate [--lenient] FILE
///   fsharp-polycall call SERVICE OPERATION --endpoint H:P [--input JSON] [--timeout-ms N]
///   fsharp-polycall peer echo --node-id ID [--endpoint H:P] [--endpoint-file F]
///                             [--peer ID=H:P ...] [--count N] [--idle-timeout-ms N]
///                             [--auth-token-env NAME]
///
/// `peer echo` is the cross-binding interop agent: every message received is
/// sent back to its sender with message id "echo-<id>". Exit codes follow the
/// polycall CLI: 0 ok, 1 failure, 2 usage, 3 config, 4 not found/unsupported,
/// 5 transport, 6 deadline, 7 auth, 8 library cannot be loaded.
module FSharpPolycall.Cli.Program

open System
open System.IO
open System.Security.Cryptography
open OBINexus.Polycall

exception Usage of string

let exitCode (status: int) =
    match enum<PolycallStatus> status with
    | PolycallStatus.Ok -> 0
    | PolycallStatus.InvalidArgument
    | PolycallStatus.TooLarge -> 2
    | PolycallStatus.Config -> 3
    | PolycallStatus.NotFound
    | PolycallStatus.Unsupported -> 4
    | PolycallStatus.Transport -> 5
    | PolycallStatus.Timeout -> 6
    | PolycallStatus.Auth -> 7
    | _ -> 1

let json (s: string) =
    let sb = Text.StringBuilder("\"")
    for c in s do
        match c with
        | '"' -> sb.Append("\\\"") |> ignore
        | '\\' -> sb.Append("\\\\") |> ignore
        | '\n' -> sb.Append("\\n") |> ignore
        | '\r' -> sb.Append("\\r") |> ignore
        | '\t' -> sb.Append("\\t") |> ignore
        | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
        | c -> sb.Append(c) |> ignore
    sb.Append('"').ToString()

let usage (w: TextWriter) =
    w.WriteLine "usage: fsharp-polycall version"
    w.WriteLine "       fsharp-polycall validate [--lenient] FILE"
    w.WriteLine "       fsharp-polycall call SERVICE OPERATION --endpoint H:P [--input JSON] [--timeout-ms N]"
    w.WriteLine "       fsharp-polycall peer echo --node-id ID [--endpoint H:P] [--endpoint-file F]"
    w.WriteLine "                                 [--peer ID=H:P ...] [--count N] [--idle-timeout-ms N]"
    w.WriteLine "                                 [--auth-token-env NAME]"
    w.WriteLine "The library is found via POLYCALL_LIBRARY, then the platform name."

/// Parse "--name value" pairs starting at index i.
let options (args: string[]) (start: int) =
    if (args.Length - start) % 2 <> 0 then raise (Usage(sprintf "option %s needs a value" args.[args.Length - 1]))
    [ for i in start .. 2 .. args.Length - 1 -> args.[i], args.[i + 1] ]

let number (name: string) (v: string) =
    match Int64.TryParse v with
    | true, n when n >= 0L -> n
    | _ -> raise (Usage(sprintf "%s must be a non-negative integer, got '%s'" name v))

let writeAtomically (file: string) (text: string) =
    let full = Path.GetFullPath file
    let tmp = full + ".tmp"
    File.WriteAllText(tmp, text)
    File.Move(tmp, full, true)

let echo (args: string[]) =
    let mutable nodeId = null
    let mutable bind = "127.0.0.1:0"
    let mutable endpointFile = null
    let mutable tokenEnv = "POLYCALL_DEV_TOKEN"
    let mutable count = 0L
    let mutable idle = 30000L
    let peers = ResizeArray<string * string>()
    for name, value in options args 2 do
        match name with
        | "--node-id" -> nodeId <- value
        | "--endpoint" -> bind <- value
        | "--endpoint-file" -> endpointFile <- value
        | "--auth-token-env" -> tokenEnv <- value
        | "--count" -> count <- number name value
        | "--idle-timeout-ms" -> idle <- number name value
        | "--peer" ->
            match value.IndexOf '=' with
            | i when i > 0 -> peers.Add(value.Substring(0, i), value.Substring(i + 1))
            | _ -> raise (Usage(sprintf "--peer needs ID=HOST:PORT, got '%s'" value))
        | other -> raise (Usage(sprintf "unknown option '%s'" other))
    if isNull nodeId then raise (Usage "peer echo needs --node-id ID")
    let token = Environment.GetEnvironmentVariable tokenEnv
    use peer = Peer.Open(nodeId, bind, (if String.IsNullOrEmpty token then null else token))
    for id, ep in peers do
        peer.Register(id, ep)
    let ep = peer.Endpoint
    if not (isNull endpointFile) then writeAtomically endpointFile (ep + "\n")
    printfn "{\"event\":\"listening\",\"node_id\":%s,\"endpoint\":%s}" (json nodeId) (json ep)
    Console.Out.Flush()
    let mutable echoed = 0L
    let mutable result = 0
    let mutable running = true
    while running && (count = 0L || echoed < count) do
        match (try Choice1Of2(peer.Recv(uint32 idle)) with :? PolycallException as e -> Choice2Of2 e) with
        | Choice2Of2 e when e.Status = int PolycallStatus.Timeout && count = 0L -> running <- false
        | Choice2Of2 e -> raise e
        | Choice1Of2 m ->
            let sha = Convert.ToHexString(SHA256.HashData m.Payload).ToLowerInvariant()
            printfn "{\"event\":\"received\",\"from\":%s,\"id\":%s,\"len\":%d,\"sha256\":\"%s\"}"
                (json m.Sender) (json m.MessageId) m.Payload.Length sha
            let replyId = "echo-" + m.MessageId
            try
                peer.Send(m.Sender, m.Payload, replyId, 10000u)
                printfn "{\"event\":\"echoed\",\"to\":%s,\"id\":%s}" (json m.Sender) (json replyId)
            with :? PolycallException as e ->
                eprintfn "fsharp-polycall: echo to %s failed: %s" m.Sender e.Message
                printfn "{\"event\":\"echo_failed\",\"to\":%s,\"status\":%s}" (json m.Sender) (json e.StatusName)
                result <- 1
            Console.Out.Flush()
            echoed <- echoed + 1L
    result

let run (args: string[]) =
    match List.ofArray args with
    | []
    | ("help" | "--help" | "-h") :: _ ->
        usage Console.Out
        0
    | [ "version" ] ->
        printfn "fsharp-polycall: libpolycall %s (binding ABI %d) loaded from %s"
            (Polycall.version ()) (Polycall.abiVersion ()) (Polycall.libraryName ())
        0
    | "validate" :: rest ->
        let lenient = List.contains "--lenient" rest
        match rest |> List.filter (fun a -> a <> "--lenient") with
        | [ file ] ->
            if lenient then Polycall.validate file else Polycall.runConfigOrRaise file
            printfn "%s is valid%s" file (if lenient then "" else " for running with this build")
            0
        | _ -> raise (Usage "usage: validate [--lenient] FILE")
    | "call" :: service :: operation :: _ ->
        let mutable endpoint = null
        let mutable input = null
        let mutable timeout = 5000L
        for name, value in options args 3 do
            match name with
            | "--endpoint" -> endpoint <- value
            | "--input" -> input <- value
            | "--timeout-ms" -> timeout <- number name value
            | other -> raise (Usage(sprintf "unknown option '%s'" other))
        if isNull endpoint then raise (Usage "call needs --endpoint H:P")
        printfn "%s" (Polycall.call endpoint service operation input (uint32 timeout))
        0
    | "peer" :: "echo" :: _ -> echo args
    | cmd :: _ -> raise (Usage(sprintf "unknown or incomplete command '%s'" cmd))

[<EntryPoint>]
let main args =
    try
        run args
    with
    | Usage msg ->
        eprintfn "fsharp-polycall: %s" msg
        usage Console.Error
        2
    | :? PolycallLoadException as e ->
        eprintfn "%s" e.Message
        8
    | :? PolycallException as e ->
        eprintfn "fsharp-polycall: %s" e.Message
        if not (isNull e.RemoteError) then eprintfn "%s" e.RemoteError
        exitCode e.Status
