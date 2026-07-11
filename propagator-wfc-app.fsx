#load "propagator-friendly.fsx"

open System
open System.Collections.Generic
open System.Diagnostics
open System.Security.Cryptography
open ``Propagator-surface-vocab``
open ``Propagator-friendly``

type Tile = Sea | Coast | Land

type Direction = East | South

type Compatibility<'tile> = Direction -> 'tile -> 'tile -> bool

type Grid<'tile when 'tile : comparison> =
    private
        { width: int
          height: int
          tiles: 'tile[]
          cells: Cell<'tile>[,]
          network: Network<'tile, 'tile, Set<'tile>> }

module Grid =

    let create width height tiles =
        if width <= 0 || height <= 0 then invalidArg "width" "grid dimensions must be positive"
        let network = Domain.finite tiles
        { width = width
          height = height
          tiles = List.toArray tiles
          cells = Array2D.init height width (fun row column -> network.Cell(sprintf "%d,%d" column row))
          network = network }

    let private isUniversal direction (compatibility: Compatibility<'tile>) (grid: Grid<'tile>) =
        grid.tiles
        |> Array.forall (fun left ->
            grid.tiles |> Array.forall (fun right -> compatibility direction left right))

    let constrain (compatibility: Compatibility<'tile>) (grid: Grid<'tile>) =
        if not (isUniversal East compatibility grid) then
            let east =
                [ for row in 0 .. grid.height - 1 do
                      for column in 0 .. grid.width - 2 ->
                          [ grid.cells.[row, column]; grid.cells.[row, column + 1] ] ]
            if not (List.isEmpty east) then
                grid.network.RelateMany(
                    east,
                    function [ left; right ] -> compatibility East left right | _ -> false)
        if not (isUniversal South compatibility grid) then
            let south =
                [ for row in 0 .. grid.height - 2 do
                      for column in 0 .. grid.width - 1 ->
                          [ grid.cells.[row, column]; grid.cells.[row + 1, column] ] ]
            if not (List.isEmpty south) then
                grid.network.RelateMany(
                    south,
                    function [ top; bottom ] -> compatibility South top bottom | _ -> false)

    let generate seed grid = grid.network.Generate seed

    let generateWith attempts seed grid = grid.network.Generate(seed, attempts)

    let observe handler grid = grid.network.Observe handler

    let cell column row grid =
        if column < 0 || column >= grid.width then invalidArg "column" "column is outside the grid"
        if row < 0 || row >= grid.height then invalidArg "row" "row is outside the grid"
        grid.cells.[row, column]

    let assume premise column row tile grid =
        grid.network.Assume(premise, cell column row grid, tile)

    let retract premise grid = grid.network.Retract premise

    let value column row grid = grid.network.Value(cell column row grid)

    let support column row grid = grid.network.Support(cell column row grid)

    let showSupport support grid = grid.network.ShowSupport support

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

    let rowMajor grid solution =
        [| for row in 0 .. grid.height - 1 do
               for column in 0 .. grid.width - 1 -> Map.find grid.cells.[row, column] solution |]

    let private writeInt32LittleEndian (bytes: byte[]) offset value =
        bytes.[offset] <- byte value
        bytes.[offset + 1] <- byte (value >>> 8)
        bytes.[offset + 2] <- byte (value >>> 16)
        bytes.[offset + 3] <- byte (value >>> 24)

    let fingerprint grid solution =
        let tiles = rowMajor grid solution
        let bytes = Array.zeroCreate<byte> (12 + tiles.Length)
        writeInt32LittleEndian bytes 0 grid.width
        writeInt32LittleEndian bytes 4 grid.height
        writeInt32LittleEndian bytes 8 tiles.Length
        tiles
        |> Array.iteri (fun index tile ->
            bytes.[12 + index] <-
                match tile with
                | Sea -> 0uy
                | Coast -> 1uy
                | Land -> 2uy)
        SHA256.HashData bytes |> Convert.ToHexString

    let smallRowMajor solution grid =
        let expected =
            [| [| Coast; Land; Sea; Coast; Land; Sea; Coast; Land |]
               [| Sea; Coast; Land; Sea; Coast; Land; Sea; Coast |]
               [| Land; Sea; Coast; Land; Sea; Coast; Land; Sea |]
               [| Coast; Land; Sea; Coast; Land; Sea; Coast; Land |]
               [| Sea; Coast; Land; Sea; Coast; Land; Sea; Coast |]
               [| Land; Sea; Coast; Land; Sea; Coast; Land; Sea |] |]
            |> Array.concat
        rowMajor grid solution = expected

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

    let universalDirectionElision () =
        let width, height = 8, 6
        let rule direction upper lower =
            match direction with
            | East -> true
            | South -> upper = Sea || lower = Coast
        let explicit = Grid.create width height [ Sea; Coast ]
        let east =
            [ for row in 0 .. height - 1 do
                  for column in 0 .. width - 2 ->
                      [ explicit.cells.[row, column]; explicit.cells.[row, column + 1] ] ]
        let south =
            [ for row in 0 .. height - 2 do
                  for column in 0 .. width - 1 ->
                      [ explicit.cells.[row, column]; explicit.cells.[row + 1, column] ] ]
        explicit.network.RelateMany(
            east,
            function [ left; right ] -> rule East left right | _ -> false)
        explicit.network.RelateMany(
            south,
            function [ top; bottom ] -> rule South top bottom | _ -> false)
        let elided = Grid.create width height [ Sea; Coast ]
        Grid.constrain rule elided
        match Grid.generate 42UL explicit, Grid.generate 42UL elided with
        | Ok explicitSolution, Ok elidedSolution ->
            rowMajor explicit explicitSolution = rowMajor elided elidedSolution
        | _ -> false

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

let private gravityRule direction upper lower =
    match direction with
    | East -> true
    | South -> upper = Sea || lower = Coast

let private threeColorRule _ left right = left <> right

module private Ramp =

    let rule _ (left: int) right = abs (left - right) <= 1

    let validMap grid solution = Oracles.validMap rule grid solution

    let private domain first last = Set.ofList [ first .. last ]

    let verify () =
        let single = Grid.create 100 1 [ 0 .. 99 ]
        Grid.constrain rule single
        Grid.assume "left" 0 0 0 single
        let singlePin =
            [ 0 .. 99 ]
            |> List.forall (fun column -> Grid.value column 0 single = domain 0 column)
        let seam = Set.count (Grid.value 63 0 single) = 64 && Set.count (Grid.value 64 0 single) = 65

        let pinned = Grid.create 100 1 [ 0 .. 99 ]
        Grid.constrain rule pinned
        Grid.assume "left" 0 0 0 pinned
        Grid.assume "right" 99 0 99 pinned
        let forced =
            [ 0 .. 99 ]
            |> List.forall (fun column -> Grid.value column 0 pinned = Set.singleton column)
        let supports =
            Grid.showSupport (Grid.support 0 0 pinned) pinned = "{left}"
            && Grid.showSupport (Grid.support 50 0 pinned) pinned = "{left, right}"
            && Grid.showSupport (Grid.support 99 0 pinned) pinned = "{right}"
        Grid.retract "left" pinned
        let retractMatchesFresh =
            let fresh = Grid.create 100 1 [ 0 .. 99 ]
            Grid.constrain rule fresh
            Grid.assume "right" 99 0 99 fresh
            [ 0 .. 99 ]
            |> List.forall (fun column ->
                Grid.value column 0 pinned = Grid.value column 0 fresh
                && Grid.showSupport (Grid.support column 0 pinned) pinned
                   = Grid.showSupport (Grid.support column 0 fresh) fresh)

        let contradicted = Grid.create 3 1 [ 0 .. 99 ]
        Grid.constrain rule contradicted
        Grid.assume "left" 0 0 0 contradicted
        Grid.assume "conflict" 2 0 50 contradicted
        let assertSiteBottom = Set.isEmpty (Grid.value 2 0 contradicted)

        singlePin && seam && forced && supports && retractMatchesFresh && assertSiteBottom

    let fingerprint grid solution =
        let values = Oracles.rowMajor grid solution
        let bytes = Array.zeroCreate<byte> (12 + values.Length * 4)
        let write offset value =
            bytes.[offset] <- byte value
            bytes.[offset + 1] <- byte (value >>> 8)
            bytes.[offset + 2] <- byte (value >>> 16)
            bytes.[offset + 3] <- byte (value >>> 24)
        write 0 grid.width
        write 4 grid.height
        write 8 values.Length
        values |> Array.iteri (fun index value -> write (12 + index * 4) value)
        SHA256.HashData bytes |> Convert.ToHexString

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
        let fingerprint = latest |> Option.map (Oracles.fingerprint grid) |> Option.defaultValue "missing"
        printfn "  %-22s best %10.3f ms  mean %10.3f ms  valid=%b  (%d trials x %d iters)  sha256=%s"
            name best mean valid trials iterations fingerprint
    printfn "== like-for-like historical constraints through Friendly (500x500, seed 42, Map included) =="
    run "gravity generation" [ Sea; Coast ] gravityRule 2
    run "3-color generation" [ Sea; Coast; Land ] threeColorRule 1

let private benchmarkRamp () =
    let trials =
        match Environment.GetEnvironmentVariable "TRIALS" with
        | null | "" -> 3
        | value -> int value
    let side = 500

    let ramp512 = Grid.create side side [ 0 .. 511 ]
    Grid.constrain Ramp.rule ramp512
    Grid.assume "corner" 0 0 0 ramp512
    let ramp512Valid =
        Set.count (Grid.value 0 0 ramp512) = 1
        && Set.count (Grid.value 10 0 ramp512) = 11
        && Set.count (Grid.value 250 250 ramp512) = 501
        && Set.count (Grid.value 499 499 ramp512) = 512

    let ramp128 = Grid.create side side [ 0 .. 127 ]
    Grid.constrain Ramp.rule ramp128
    Grid.assume "center" 250 250 0 ramp128
    let ramp128Valid =
        Set.count (Grid.value 250 250 ramp128) = 1
        && Set.count (Grid.value 251 250 ramp128) = 2
        && Set.count (Grid.value 0 0 ramp128) = 128

    if not (ramp512Valid && ramp128Valid) then
        failwithf "Friendly ramp propagation spot checks failed (ramp512=%b, ramp128=%b)" ramp512Valid ramp128Valid

    let best512, mean512 = sample 1 trials 1 (fun () -> Grid.assume "corner" 0 0 0 ramp512)
    let best128, mean128 = sample 1 trials 1 (fun () -> Grid.assume "center" 250 250 0 ramp128)

    let retractGrid = Grid.create side side [ 0 .. 511 ]
    Grid.constrain Ramp.rule retractGrid
    let prepareRetract () =
        Grid.assume "left" 0 0 0 retractGrid
        Grid.assume "right" 499 499 511 retractGrid
    let retractOnce () = Grid.retract "left" retractGrid
    prepareRetract ()
    retractOnce ()
    let retractMatchesFresh =
        let fresh = Grid.create side side [ 0 .. 511 ]
        Grid.constrain Ramp.rule fresh
        Grid.assume "right" 499 499 511 fresh
        [ 0, 0; 250, 250; 499, 499 ]
        |> List.forall (fun (column, row) ->
            Grid.value column row retractGrid = Grid.value column row fresh
            && Grid.showSupport (Grid.support column row retractGrid) retractGrid
               = Grid.showSupport (Grid.support column row fresh) fresh)
    if not retractMatchesFresh then failwith "Friendly ramp retraction did not match a fresh surviving-premise twin"

    for _ in 1 .. 1 do prepareRetract (); retractOnce ()
    let mutable retractBest = Double.MaxValue
    let mutable retractTotal = 0.0
    for _ in 1 .. trials do
        GC.Collect()
        GC.WaitForPendingFinalizers()
        prepareRetract ()
        let timer = Stopwatch.StartNew()
        retractOnce ()
        timer.Stop()
        retractBest <- min retractBest timer.Elapsed.TotalMilliseconds
        retractTotal <- retractTotal + timer.Elapsed.TotalMilliseconds

    let ramp32 = Grid.create 64 64 [ 0 .. 31 ]
    Grid.constrain Ramp.rule ramp32
    let mutable latest = None
    let generate () =
        match Grid.generate 42UL ramp32 with
        | Ok solution -> latest <- Some solution
        | Error failure -> failwithf "Friendly ramp32 generation failed: %A" failure
    generate ()
    if not (latest |> Option.exists (Ramp.validMap ramp32)) then failwith "Friendly ramp32 generation produced an invalid map"
    let generationBest, generationMean = sample 1 trials 1 generate
    let generationValid = latest |> Option.exists (Ramp.validMap ramp32)
    let generationFingerprint = latest |> Option.map (Ramp.fingerprint ramp32) |> Option.defaultValue "missing"

    printfn "== historical ramp shapes through Friendly/Optimized =="
    printfn "  %-24s best %10.3f ms  mean %10.3f ms  valid=%b  (%d trials)" "ramp512 corner" best512 mean512 ramp512Valid trials
    printfn "  %-24s best %10.3f ms  mean %10.3f ms  valid=%b  (%d trials)" "ramp128 center" best128 mean128 ramp128Valid trials
    printfn "  %-24s best %10.3f ms  mean %10.3f ms  valid=%b  (%d trials)" "ramp512 retract" retractBest (retractTotal / float trials) retractMatchesFresh trials
    printfn "  %-24s best %10.3f ms  mean %10.3f ms  valid=%b  (%d trials)  sha256=%s" "ramp32 generation" generationBest generationMean generationValid trials generationFingerprint

let main () =
    printfn "runtime: %s" Runtime.InteropServices.RuntimeInformation.FrameworkDescription
    let grid = Grid.create 8 6 [ Sea; Coast; Land ]
    Grid.constrain compatibility grid
    let snapshots, subscription = Oracles.replay grid
    let generated = Grid.generate 42UL grid
    let valid, replay, smallExact =
        match generated with
        | Ok solution ->
            Grid.render solution grid |> List.iter (printfn "  %s")
            Oracles.validMap compatibility grid solution,
            Oracles.replayMatches grid solution snapshots,
            Oracles.smallRowMajor solution grid
        | Error failure ->
            printfn "  generation failed: %A" failure
            false, false, false
    subscription.Dispose()
    let crossRoute = Oracles.crossRouteSeed ()
    let restart = Oracles.restartExhaustion ()
    let universalElision = Oracles.universalDirectionElision ()
    let ramp = Ramp.verify ()
    printfn "map validity: %b; snapshot replay: %b; exact row-major: %b; cross-route seed: %b; bounded failure: %b; universal elision: %b; ramp laws: %b"
        valid replay smallExact crossRoute restart universalElision ramp
    let ok = valid && replay && smallExact && crossRoute && restart && universalElision && ramp
    if ok && fsi.CommandLineArgs |> Array.contains "--benchmark" then
        benchmark (fsi.CommandLineArgs |> Array.contains "--benchmark-large")
    if ok && fsi.CommandLineArgs |> Array.contains "--benchmark-like-for-like" then
        benchmarkLikeForLike ()
    if ok && fsi.CommandLineArgs |> Array.contains "--benchmark-ramp" then
        benchmarkRamp ()
    printfn "WFC application: %s" (if ok then "PASS" else "FAIL")
    if ok then 0 else 1

exit (main ())
