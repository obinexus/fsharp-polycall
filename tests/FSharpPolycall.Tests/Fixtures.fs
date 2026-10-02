/// Real-process fixtures: the installed polycall CLI, runtimes and peer nodes.
module Fixtures

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.RegularExpressions
open System.Threading
open Expecto
open OBINexus.Polycall

/// Random shared token for this test run (never a real secret).
let token = "qa-token-" + Guid.NewGuid().ToString("N")
let T = 5000u
let isWindows = OperatingSystem.IsWindows()

/// The polycall CLI: $POLYCALL_CLI, else polycall(.exe) on PATH.
let cli () : string option =
    match Environment.GetEnvironmentVariable "POLYCALL_CLI" with
    | p when not (String.IsNullOrWhiteSpace p) -> if File.Exists p then Some p else None
    | _ ->
        let exe = if isWindows then "polycall.exe" else "polycall"
        (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun d -> Path.Combine(d, exe))
        |> Array.tryFind File.Exists

/// The CLI, or mark the test SKIPPED (ignored, never passed) when it is absent.
let requireCli () =
    match cli () with
    | Some p -> p
    | None -> skiptest "polycall CLI not found (set POLYCALL_CLI or put polycall on PATH)"

let tempDir () = Directory.CreateTempSubdirectory("fsharp-polycall-").FullName

let private configure (psi: ProcessStartInfo) (extra: (string * string option) list) =
    psi.UseShellExecute <- false
    psi.Environment.["POLYCALL_DEV_TOKEN"] <- token
    psi.Environment.["POLYCALL_TELEMETRY"] <- "off"
    for k, v in extra do
        match v with
        | Some v -> psi.Environment.[k] <- v
        | None -> psi.Environment.Remove k |> ignore

/// A started background process with its captured output and endpoint.
type Proc(proc: Process, output: ConcurrentQueue<string>, endpoint: string) =
    member _.Process = proc
    member _.Endpoint = endpoint
    member _.Log = String.Join("\n", output)
    interface IDisposable with
        member _.Dispose() =
            try
                if not proc.HasExited then
                    proc.Kill(true)
                    proc.WaitForExit 5000 |> ignore
            with _ -> ()

let waitForLine (file: string) (owner: Process) (limitMs: int) : string option =
    let sw = Stopwatch.StartNew()
    let mutable result = None
    while result.IsNone && sw.ElapsedMilliseconds < int64 limitMs && not (owner.HasExited && not (File.Exists file)) do
        if File.Exists file then
            let s = try File.ReadAllText file with _ -> ""
            if s.EndsWith "\n" || Regex.IsMatch(s.Trim(), @"^.+:\d+$") then result <- Some(s.Trim())
        if result.IsNone then Thread.Sleep 50
    result

/// Start a process that writes its endpoint to `--endpoint-file F` (appended).
let start (dir: string) (name: string) (exe: string) (args: string list) : Proc =
    let epFile = Path.Combine(dir, name + ".ep")
    let psi = ProcessStartInfo(exe)
    for a in args @ [ "--endpoint-file"; epFile ] do
        psi.ArgumentList.Add a
    psi.WorkingDirectory <- dir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    configure psi []
    let output = ConcurrentQueue<string>()
    let p = new Process(StartInfo = psi)
    p.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then output.Enqueue e.Data)
    p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then output.Enqueue("stderr: " + e.Data))
    p.Start() |> ignore
    p.BeginOutputReadLine()
    p.BeginErrorReadLine()
    match waitForLine epFile p 20000 with
    | Some ep -> new Proc(p, output, ep)
    | None ->
        (try p.Kill(true) with _ -> ())
        failtestf "%s did not report an endpoint: %s" name (String.Join("\n", output))

/// polycall start on an ephemeral loopback port.
let startRuntime (dir: string) =
    start dir "runtime" (requireCli ()) [ "start"; "--endpoint"; "127.0.0.1:0" ]

/// polycall peer serve (the C CLI node) on an ephemeral loopback port.
let startCliPeer (dir: string) (nodeId: string) =
    start dir ("peer-" + nodeId) (requireCli ()) [ "peer"; "serve"; "--node-id"; nodeId; "--endpoint"; "127.0.0.1:0" ]

type Result = { Exit: int; Stdout: byte[]; Stderr: string }
    with member r.Out = Encoding.UTF8.GetString r.Stdout

/// Run a command to completion with the test token in its environment.
let run (exe: string) (args: string list) (extra: (string * string option) list) (limitMs: int) : Result =
    let psi = ProcessStartInfo(exe)
    for a in args do
        psi.ArgumentList.Add a
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.RedirectStandardInput <- true
    configure psi extra
    use p = Process.Start psi
    p.StandardInput.Close()
    use ms = new MemoryStream()
    let copy = p.StandardOutput.BaseStream.CopyToAsync ms
    let err = p.StandardError.ReadToEndAsync()
    if not (p.WaitForExit limitMs) then
        p.Kill(true)
        failtestf "timed out: %s %A" exe args
    copy.Wait()
    { Exit = p.ExitCode; Stdout = ms.ToArray(); Stderr = err.Result }

let jsonString (json: string) (key: string) : string option =
    let m = Regex.Match(json, "\"" + Regex.Escape key + "\":\"((?:[^\"\\\\]|\\\\.)*)\"")
    if m.Success then Some m.Groups.[1].Value else None

/// Assert that f raises PolycallException with this status; return it.
let expectStatus (status: PolycallStatus) (f: unit -> 'a) : PolycallException =
    let e =
        try
            f () |> ignore
            failtestf "expected %A, but the call succeeded" status
        with :? PolycallException as e -> e
    Expect.equal e.StatusCode status e.Message
    Expect.equal e.StatusName ((Polycall.strerror (int status)).Split(':').[0]) "status name"
    e

let freePort () =
    let l = new TcpListener(IPAddress.Loopback, 0)
    l.Start()
    let port = (l.LocalEndpoint :?> IPEndPoint).Port
    l.Stop()
    port

let bytes (s: string) = Encoding.UTF8.GetBytes s

let expectMessage (m: PeerMessage) sender id (payload: byte[]) =
    Expect.equal m.Sender sender "sender id"
    Expect.equal m.MessageId id "message id"
    Expect.isTrue (m.Payload = payload) (sprintf "payload bytes (%d vs %d)" m.Payload.Length payload.Length)
