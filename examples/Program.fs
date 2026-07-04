open OBINexus.Polycall

[<EntryPoint>]
let main args =
    let configPath =
        if args.Length > 0 then args[0] else Polycall.DefaultConfig

    Polycall.runConfigOrRaise configPath
    printfn "libpolycall completed '%s' successfully" configPath
    0
