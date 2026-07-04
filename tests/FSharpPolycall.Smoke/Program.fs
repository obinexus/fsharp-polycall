open OBINexus.Polycall

let private assertEqual expected actual message =
    if actual <> expected then
        failwith $"{message}: expected {expected}, got {actual}"

[<EntryPoint>]
let main _ =
    assertEqual 0 (Polycall.runConfig "fsharp-polycallrc") "success status"

    match Polycall.tryRunConfig "__status_37__" with
    | Error 37 -> ()
    | result -> failwith $"expected Error 37, got {result}"

    try
        Polycall.runConfigOrRaise "__status_37__"
        failwith "runConfigOrRaise should have raised PolycallException"
    with
    | :? PolycallException as error ->
        assertEqual 37 error.Status "exception status"
        assertEqual "__status_37__" error.ConfigPath "exception config path"

    printfn "fsharp-polycall F#/PInvoke smoke test: PASS"
    0
