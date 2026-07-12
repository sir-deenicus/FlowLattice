open AsyncRxHopacTests
open AsyncSeqInteropTests

[<EntryPoint>]
let main (_argv: string array) =
    printfn "=== AsyncRxHopac tests ==="
    let kernelFailures = AsyncRxHopacTests.run ()

    printfn "\n=== AsyncSeq interop tests ==="
    let interopFailures = AsyncSeqInteropTests.run ()

    max kernelFailures interopFailures
