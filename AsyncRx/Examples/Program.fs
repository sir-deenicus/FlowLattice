open System
open AsyncRxHopacExamples
open AsyncSeqInteropExamples

let private runExample (title: string) (example: unit -> unit) =
    printfn "\nPress Enter to run %s..." title
    Console.ReadLine() |> ignore
    printfn "\n== %s ==" title
    example ()

[<EntryPoint>]
let main (_argv: string array) =
    runExample "productMergeDemo" productMergeDemo
    runExample "asyncRxBuilderDemo" asyncRxBuilderDemo
    runExample "branchChoiceDemo" branchChoiceDemo
    runExample "patternLikeDemo" patternLikeDemo
    runExample "backpressureAndCancellationDemo" backpressureAndCancellationDemo
    runExample "backpressureAndCancellationDemoWithAsyncRxHelpers" backpressureAndCancellationDemoWithAsyncRxHelpers
    runExample "thermostatDemo" thermostatDemo
    0
