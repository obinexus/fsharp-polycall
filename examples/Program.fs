open OBINexus.Polycall

/// Validate a configuration for running, then exchange one message between
/// two peer nodes in this process.
[<EntryPoint>]
let main args =
    let configPath = if args.Length > 0 then args[0] else Polycall.DefaultConfig
    printfn "libpolycall %s (binding ABI %d) from %s"
        (Polycall.version ()) (Polycall.abiVersion ()) (Polycall.libraryName ())
    Polycall.runConfigOrRaise configPath
    printfn "'%s' is valid for running with this build" configPath

    use alpha = Peer.Open("alpha", "127.0.0.1:0")
    use beta = Peer.Open("beta", "127.0.0.1:0")
    alpha.Register("beta", beta.Endpoint)
    alpha.SendText("beta", "hello from F#", "example-1", 5000u)
    let m = beta.Recv 5000u
    printfn "beta received '%s' from %s (id %s)" m.Text m.Sender m.MessageId
    0
