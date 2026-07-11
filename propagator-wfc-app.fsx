#load "propagator-friendly.fsx"

open System
open System.Collections.Generic
open System.Diagnostics
open ``Propagator-surface-vocab``
open ``Propagator-friendly``

type Tile = Sea | Coast | Land

type Direction = East | South

type Compatibility = Direction -> Tile -> Tile -> bool

type Grid =
    private
        { width: int
          height: int
          cells: Cell<Tile>[,]
          network: Network<Tile, Tile, Set<Tile>> }

module Grid =

    let create width height tiles =
        if width <= 0 || height <= 0 then invalidArg "width" "grid dimensions must be positive"
        let network = Domain.finite tiles
        { width = width
          height = height
          cells = Array2D.init height width (fun row column -> network.Cell(sprintf "%d,%d" column row))
          network = network }

    let constrain (compatibility: Compatibility) grid =
        let east =
            [ for row in 0 .. grid.height - 1 do
                  for column in 0 .. grid.width - 2 ->
                      [ grid.cells.[row, column]; grid.cells.[row, column + 1] ] ]
        let south =
            [ for row in 0 .. grid.height - 2 do
                  for column in 0 .. grid.width - 1 ->
                      [ grid.cells.[row, column]; grid.cells.[row + 1, column] ] ]
        if not (List.isEmpty east) then
            grid.network.RelateMany(
                east,
                function [ left; right ] -> compatibility East left right | _ -> false)
        if not (List.isEmpty south) then
            grid.network.RelateMany(
                south,
                function [ top; bottom ] -> compatibility South top bottom | _ -> false)

    let generate seed grid = grid.network.Generate seed

    let generateWith attempts seed grid = grid.network.Generate(seed, attempts)

    let observe handler grid = grid.network.Observe handler

    let cells grid =
        [ for row in 0 .. grid.height - 1 do
              for column in 0 .. grid.width - 1 -> grid.cells.[row, column] ]

    let render solution grid =
        let symbol = function Sea -> '~' | Coast -> '+' | Land -> '#'
        [ for row in 0 .. grid.height - 1 ->
              String [| for column in 0 .. grid.width - 1 -> symbol (Map.find grid.cells.[row, column] solution) |] ]

let compatibility direction left right =
    match direction, left, right with
    | East, Sea, Coast | East, Coast, Land | East, Land, Sea -> true
    | South, Sea, Land | South, Land, Coast | South, Coast, Sea -> true
    | _ -> false

module private Oracles =

    let validMap compatibility grid solution =
        let mutable valid = true
        for row in 0 .. grid.height - 1 do
            for column in 0 .. grid.width - 1 do
                let current = Map.find grid.cells.[row, column] solution
                if column + 1 < grid.width then
                    valid <- valid && compatibility East current (Map.find grid.cells.[row, column + 1] solution)
                if row + 1 < grid.height then
                    valid <- valid && compatibility South current (Map.find grid.cells.[row + 1, column] solution)
        valid && Map.count solution = grid.width * grid.height

    let replay grid =
        let snapshots = Dictionary<Cell<Tile>, CellState<Tile>>()
        let subscription = Grid.observe (fun (cell, state) -> snapshots.[cell] <- state) grid
        snapshots, subscription

    let replayMatches grid solution (snapshots: Dictionary<Cell<Tile>, CellState<Tile>>) =
        snapshots.Count = grid.width * grid.height
        && snapshots
           |> Seq.forall (fun pair ->
               match pair.Value with
               | FiniteCandidates [ tile ] -> Map.find pair.Key solution = tile
               | _ -> false)

    let crossRouteSeed () =
        let width, height = 4, 3
        let cells = Array2D.init height width (fun row column -> Cell.create(sprintf "oracle-%d,%d" column row))
        let east =
            [ for row in 0 .. height - 1 do
                  for column in 0 .. width - 2 -> [ cells.[row, column]; cells.[row, column + 1] ] ]
        let south =
            [ for row in 0 .. height - 2 do
                  for column in 0 .. width - 1 -> [ cells.[row, column]; cells.[row + 1, column] ] ]
        let model =
            { domain = ``Propagator-surface-vocab``.Domain.finite [ Sea; Coast; Land ]
              cells = [ for row in 0 .. height - 1 do for column in 0 .. width - 1 -> cells.[row, column] ]
              constraints =
                  [ Constraint.relations east (function
                        | [ left; right ] -> compatibility East left right
                        | _ -> false)
                    Constraint.relations south (function
                        | [ top; bottom ] -> compatibility South top bottom
                        | _ -> false) ]
              givens = [] }
        match General.lower model, Optimized.lower model with
        | Ok general, Ok optimized ->
            General.generateWith 4 42UL general = Optimized.generateWith 4 42UL optimized
        | _ -> false

    let restartExhaustion () =
        let grid = Grid.create 2 1 [ Sea; Coast ]
        Grid.constrain (fun _ _ _ -> false) grid
        Grid.generateWith 3 1UL grid = Error (RestartLimitExceeded 3)

let private timed action =
    GC.Collect()
    GC.WaitForPendingFinalizers()
    let timer = Stopwatch.StartNew()
    let result = action ()
    timer.Stop()
    result, timer.Elapsed.TotalMilliseconds

let private sample warmup trials iterations action =
    for _ in 1 .. warmup do action ()
    let samples =
        [ for _ in 1 .. trials do
              GC.Collect()
              GC.WaitForPendingFinalizers()
              let timer = Stopwatch.StartNew()
              for _ in 1 .. iterations do action ()
              timer.Stop()
              yield timer.Elapsed.TotalMilliseconds / float iterations ]
    List.min samples, List.average samples

let private benchmark includeLarge =
    printfn "== new Friendly WFC generation timing (solution Map included) =="
    let sizes = if includeLarge then [ 16; 32; 64; 128; 500 ] else [ 16; 32; 64; 128 ]
    for side in sizes do
        let grid = Grid.create side side [ Sea; Coast; Land ]
        Grid.constrain compatibility grid
        let result, elapsed = timed (fun () -> Grid.generate 42UL grid)
        let valid = result |> Result.map (Oracles.validMap compatibility grid) = Ok true
        printfn "  %3dx%-3d  %10.3f ms  valid=%b" side side elapsed valid

let private benchmarkLikeForLike () =
    let trials =
        match Environment.GetEnvironmentVariable "TRIALS" with
        | null | "" -> 3
        | value -> int value
    let side = 500
    let run name tiles rule iterations =
        let grid = Grid.create side side tiles
        Grid.constrain rule grid
        let mutable latest = None
        let generate () =
            match Grid.generate 42UL grid with
            | Ok solution -> latest <- Some solution
            | result -> failwithf "%s generation failed validity: %A" name result
        generate ()
        match latest with
        | Some solution when Oracles.validMap rule grid solution -> ()
        | _ -> failwithf "%s warmup failed validity" name
        let best, mean = sample 1 trials iterations generate
        let valid = latest |> Option.exists (Oracles.validMap rule grid)
        printfn "  %-22s best %10.3f ms  mean %10.3f ms  valid=%b  (%d trials x %d iters)"
            name best mean valid trials iterations
    let gravity direction upper lower =
        match direction with
        | East -> true
        | South -> upper = Sea || lower = Coast
    let threeColor _ left right = left <> right
    printfn "== like-for-like historical constraints through Friendly (500x500, seed 42, Map included) =="
    run "gravity generation" [ Sea; Coast ] gravity 2
    run "3-color generation" [ Sea; Coast; Land ] threeColor 1

let main () =
    printfn "runtime: %s" Runtime.InteropServices.RuntimeInformation.FrameworkDescription
    let grid = Grid.create 8 6 [ Sea; Coast; Land ]
    Grid.constrain compatibility grid
    let snapshots, subscription = Oracles.replay grid
    let generated = Grid.generate 42UL grid
    let valid, replay =
        match generated with
        | Ok solution ->
            Grid.render solution grid |> List.iter (printfn "  %s")
            Oracles.validMap compatibility grid solution,
            Oracles.replayMatches grid solution snapshots
        | Error failure ->
            printfn "  generation failed: %A" failure
            false, false
    subscription.Dispose()
    let crossRoute = Oracles.crossRouteSeed ()
    let restart = Oracles.restartExhaustion ()
    printfn "map validity: %b; snapshot replay: %b; cross-route seed: %b; bounded failure: %b"
        valid replay crossRoute restart
    let ok = valid && replay && crossRoute && restart
    if ok && fsi.CommandLineArgs |> Array.contains "--benchmark" then
        benchmark (fsi.CommandLineArgs |> Array.contains "--benchmark-large")
    if ok && fsi.CommandLineArgs |> Array.contains "--benchmark-like-for-like" then
        benchmarkLikeForLike ()
    printfn "WFC application: %s" (if ok then "PASS" else "FAIL")
    if ok then 0 else 1

exit (main ())
