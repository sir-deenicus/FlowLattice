module Propagator.Optimized

open System.Collections.Generic
open System.Numerics
open Propagator.Core

/// A lowered optimized finite network. Its private closures retain one live runtime-width store.
type OptimizedNet<'a> =
    private
        { addCell: string -> Cell<'a>
          addConstraint: Constraint<'a> -> unit
          freshPremise: unit -> Premise
          assertState: Premise -> Cell<'a> -> CellState<'a> -> unit
          retractPremise: Premise -> unit
          readState: Cell<'a> -> CellState<'a>
          readSupport: Cell<'a> -> Set<Premise>
          run: unit -> Solution<'a> option
          generate: uint64 -> int -> Result<Solution<'a>, GenerationFailure>
          observeCell: Cell<'a> -> (Cell<'a> * CellState<'a> -> unit) -> System.IDisposable
          observeNet: (Cell<'a> * CellState<'a> -> unit) -> System.IDisposable }


module private FiniteCore =

    type Table =
        { forward: uint64[]
          reverse: uint64[]
          forwardFull: bool[]
          reverseFull: bool[] }

    [<Struct>]
    type Arc =
        { source: int
          target: int
          table: int
          reverse: bool }

    type Nary<'a> =
        { cells: int[]
          allows: 'a list -> bool }

    type Engine<'a when 'a : equality>
        (domain: 'a[], publicCells: Cell<'a>[], constraints: Constraint<'a> list) =

        let domainCount = domain.Length
        let wordCount = max 1 ((domainCount + 63) / 64)
        let finalMask =
            let used = domainCount % 64
            if used = 0 then System.UInt64.MaxValue else (1UL <<< used) - 1UL
        let top = Array.init wordCount (fun word -> if word = wordCount - 1 then finalMask else System.UInt64.MaxValue)
        let valueIndex = Dictionary<'a, int>()
        let cellIndex = Dictionary<int, int>()
        do
            domain |> Array.iteri (fun index value -> valueIndex.Add(value, index))
            publicCells |> Array.iteri (fun index cell -> cellIndex.Add(cell.id, index))

        let encode (items: 'a list) =
            let words = Array.zeroCreate<uint64> wordCount
            for item in items do
                match valueIndex.TryGetValue item with
                | true, index -> words.[index / 64] <- words.[index / 64] ||| (1UL <<< (index % 64))
                | _ -> invalidArg "items" "candidate is not in the authored finite domain"
            words

        let tables, arcs, naries, watches =
            let tables = ResizeArray<Table>()
            let arcs = ResizeArray<Arc>()
            let naries = ResizeArray<Nary<'a>>()
            let watches = Array.init publicCells.Length (fun _ -> ResizeArray<int>())
            let addArc source target table reverse =
                let id = arcs.Count
                arcs.Add { source = source; target = target; table = table; reverse = reverse }
                watches.[source].Add id
            for constraintBox in constraints do
                match constraintBox with
                | Dataflow _ -> invalidOp "optimized finite networks do not support dataflow constraints"
                | Relation box when box.arity = 2 ->
                    let forward = Array.zeroCreate<uint64> (domainCount * wordCount)
                    let reverse = Array.zeroCreate<uint64> (domainCount * wordCount)
                    for left in 0 .. domainCount - 1 do
                        for right in 0 .. domainCount - 1 do
                            if box.allows [ domain.[left]; domain.[right] ] then
                                forward.[left * wordCount + right / 64] <-
                                    forward.[left * wordCount + right / 64] ||| (1UL <<< (right % 64))
                                reverse.[right * wordCount + left / 64] <-
                                    reverse.[right * wordCount + left / 64] ||| (1UL <<< (left % 64))
                    let fullRows (rows: uint64[]) =
                        Array.init domainCount (fun value ->
                            let offset = value * wordCount
                            let mutable full = true
                            let mutable word = 0
                            while full && word < wordCount do
                                full <- rows.[offset + word] = top.[word]
                                word <- word + 1
                            full)
                    let tableId = tables.Count
                    tables.Add
                        { forward = forward
                          reverse = reverse
                          forwardFull = fullRows forward
                          reverseFull = fullRows reverse }
                    for offset in 0 .. box.arity .. box.scopes.Length - 1 do
                        let left = cellIndex.[box.scopes.[offset].id]
                        let right = cellIndex.[box.scopes.[offset + 1].id]
                        addArc left right tableId false
                        addArc right left tableId true
                | Relation box ->
                    for offset in 0 .. box.arity .. box.scopes.Length - 1 do
                        let ids =
                            [| for index in offset .. offset + box.arity - 1 -> cellIndex.[box.scopes.[index].id] |]
                        let id = naries.Count
                        naries.Add { cells = ids; allows = box.allows }
                        let work = -(id + 1)
                        for cell in ids do watches.[cell].Add work
            tables.ToArray(), arcs.ToArray(), naries.ToArray(), watches |> Array.map _.ToArray()

        let state = Array.zeroCreate<uint64> (publicCells.Length * wordCount)
        let supports = Array.zeroCreate<uint64> publicCells.Length
        // Static no-assertion fixpoint; rebuild restores this before replaying authored premises.
        let structuralState = Array.zeroCreate<uint64> state.Length
        let mutable baselineState: uint64[] = null
        let mutable baselineSupports: uint64[] = null
        let mutable baselineValid = false
        let assertions = Dictionary<struct(int * int), uint64[]>()
        let generated = Dictionary<int, uint64[]>()
        let handlers = Dictionary<int, int -> unit>()
        let mutable nextHandler = 0
        let queue = Queue<int>()
        let queuedArcs = Array.zeroCreate<bool> arcs.Length
        let queuedNaries = Array.zeroCreate<bool> naries.Length

        let wordOffset cell = cell * wordCount
        let hasValue cell value =
            state.[wordOffset cell + value / 64] &&& (1UL <<< (value % 64)) <> 0UL
        let notify cell = for handler in handlers.Values do handler cell
        let enqueue work =
            if work >= 0 then
                if not queuedArcs.[work] then
                    queuedArcs.[work] <- true
                    queue.Enqueue work
            else
                let id = -work - 1
                if not queuedNaries.[id] then
                    queuedNaries.[id] <- true
                    queue.Enqueue work
        let enqueueCell cell = for work in watches.[cell] do enqueue work

        let meetCell cell (mask: uint64[]) support =
            let offset = wordOffset cell
            let mutable changed = false
            for word in 0 .. wordCount - 1 do
                let narrowed = state.[offset + word] &&& mask.[word]
                if narrowed <> state.[offset + word] then
                    state.[offset + word] <- narrowed
                    changed <- true
            if changed then
                supports.[cell] <- supports.[cell] ||| support
                notify cell
                enqueueCell cell
            changed

        let meetCellWord cell mask support =
            let offset = wordOffset cell
            let narrowed = state.[offset] &&& mask
            if narrowed <> state.[offset] then
                state.[offset] <- narrowed
                supports.[cell] <- supports.[cell] ||| support
                notify cell
                enqueueCell cell
                true
            else
                false

        let candidates cell =
            [ for index in 0 .. domainCount - 1 do if hasValue cell index then yield domain.[index] ]

        let fireArc (scratch: uint64[]) id =
            let arc = arcs.[id]
            let table = tables.[arc.table]
            let rows = if arc.reverse then table.reverse else table.forward
            let fullRows = if arc.reverse then table.reverseFull else table.forwardFull
            if wordCount = 1 then
                let mutable allowed = 0UL
                let mutable value = 0
                let mutable complete = false
                while value < domainCount && not complete do
                    if hasValue arc.source value then
                        if fullRows.[value] then
                            allowed <- top.[0]
                            complete <- true
                        else
                            allowed <- allowed ||| rows.[value]
                    value <- value + 1
                meetCellWord arc.target allowed supports.[arc.source] |> ignore
            else
                System.Array.Clear(scratch, 0, wordCount)
                let sourceOffset = wordOffset arc.source
                let mutable sourceWord = 0
                let mutable complete = false
                while sourceWord < wordCount && not complete do
                    let mutable bits = state.[sourceOffset + sourceWord]
                    while bits <> 0UL && not complete do
                        let bit = int (BitOperations.TrailingZeroCount bits)
                        let value = sourceWord * 64 + bit
                        bits <- bits &&& (bits - 1UL)
                        if fullRows.[value] then
                            System.Array.Copy(top, scratch, wordCount)
                            complete <- true
                        else
                            let row = value * wordCount
                            for word in 0 .. wordCount - 1 do
                                scratch.[word] <- scratch.[word] ||| rows.[row + word]
                    sourceWord <- sourceWord + 1
                meetCell arc.target scratch supports.[arc.source] |> ignore

        let fireNary id =
            let relation = naries.[id]
            let before = relation.cells |> Array.map candidates |> Array.toList
            let after = Gac.narrow relation.allows before
            let support = relation.cells |> Array.fold (fun value cell -> value ||| supports.[cell]) 0UL
            Array.iter2 (fun cell narrowed -> meetCell cell (encode narrowed) support |> ignore)
                relation.cells (List.toArray after)

        let quiesce () =
            // Invocation-local ownership keeps nested propagation from observer callbacks independent.
            let scratch = if wordCount = 1 then null else Array.zeroCreate<uint64> wordCount
            while queue.Count > 0 do
                let work = queue.Dequeue()
                if work >= 0 then
                    queuedArcs.[work] <- false
                    fireArc scratch work
                else
                    let id = -work - 1
                    queuedNaries.[id] <- false
                    fireNary id

        let enqueueAll () =
            for id in 0 .. arcs.Length - 1 do enqueue id
            for id in 0 .. naries.Length - 1 do enqueue (-id - 1)

        let resetQueue () =
            queue.Clear()
            System.Array.Clear(queuedArcs, 0, queuedArcs.Length)
            System.Array.Clear(queuedNaries, 0, queuedNaries.Length)

        let captureBaseline () =
            if isNull baselineState then
                baselineState <- Array.zeroCreate<uint64> state.Length
                baselineSupports <- Array.zeroCreate<uint64> supports.Length
            System.Array.Copy(state, baselineState, state.Length)
            System.Array.Copy(supports, baselineSupports, supports.Length)
            baselineValid <- true

        let restoreBaseline () =
            if not baselineValid then invalidOp "generated baseline is not available"
            resetQueue ()
            for cell in 0 .. publicCells.Length - 1 do
                let offset = wordOffset cell
                let mutable changed = false
                for word in 0 .. wordCount - 1 do
                    if state.[offset + word] <> baselineState.[offset + word] then changed <- true
                    state.[offset + word] <- baselineState.[offset + word]
                supports.[cell] <- baselineSupports.[cell]
                if changed then notify cell

        let restoreStructural cell =
            let offset = wordOffset cell
            let mutable changed = false
            for word in 0 .. wordCount - 1 do
                if state.[offset + word] <> structuralState.[offset + word] then changed <- true
                state.[offset + word] <- structuralState.[offset + word]
            supports.[cell] <- 0UL
            if changed then notify cell

        let rebuild includeGenerated =
            resetQueue ()
            for cell in 0 .. publicCells.Length - 1 do restoreStructural cell
            assertions
            |> Seq.sortBy (fun pair -> let struct(premise, cell) = pair.Key in premise, cell)
            |> Seq.iter (fun pair ->
                let struct(premise, cell) = pair.Key
                meetCell cell pair.Value (1UL <<< premise) |> ignore)
            if includeGenerated then
                generated |> Seq.sortBy _.Key |> Seq.iter (fun pair -> meetCell pair.Key pair.Value 0UL |> ignore)
            quiesce ()

        do
            for cell in 0 .. publicCells.Length - 1 do
                let offset = wordOffset cell
                System.Array.Copy(top, 0, state, offset, wordCount)
            enqueueAll ()
            quiesce ()
            System.Array.Copy(state, structuralState, state.Length)

        member _.CellIndex (cell: Cell<'a>) =
            match cellIndex.TryGetValue cell.id with
            | true, index -> index
            | _ -> invalidArg "cell" "cell does not belong to this network"

        member _.Candidates cell = candidates cell
        member _.CandidateCount cell =
            let offset = wordOffset cell
            let mutable count = 0
            for word in 0 .. wordCount - 1 do count <- count + int (BitOperations.PopCount state.[offset + word])
            count
        member _.SingletonValue cell =
            let offset = wordOffset cell
            let mutable found = -1
            for word in 0 .. wordCount - 1 do
                let bits = state.[offset + word]
                if bits <> 0UL then
                    if found >= 0 || (bits &&& (bits - 1UL)) <> 0UL then
                        invalidOp "singleton value requested from a non-singleton cell"
                    found <- word * 64 + int (BitOperations.TrailingZeroCount bits)
            if found < 0 || found >= domainCount then
                invalidOp "singleton value requested from a non-singleton cell"
            domain.[found]
        member this.IsBottom cell = this.CandidateCount cell = 0
        member this.IsSingleton cell = this.CandidateCount cell = 1
        member _.Support cell = supports.[cell]

        member _.Assert (premise: int, cell: int, items: 'a list) =
            if premise < 0 || premise >= 64 then invalidArg "premise" "premise bitmask caps at 64"
            let key = struct(premise, cell)
            let replacing = assertions.ContainsKey key
            let encoded = encode items
            baselineValid <- false
            assertions.[key] <- encoded
            if replacing then rebuild true
            else
                meetCell cell assertions.[key] (1UL <<< premise) |> ignore
                quiesce ()

        member _.Retract premise =
            if premise < 0 || premise >= 64 then invalidArg "premise" "premise bitmask caps at 64"
            baselineValid <- false
            let dead =
                [ for pair in assertions do
                      let struct(found, _) = pair.Key
                      if found = premise then yield pair.Key ]
            for key in dead do assertions.Remove key |> ignore
            rebuild true

        member _.Collapse (cell, value: 'a) =
            let mask = encode [ value ]
            generated.[cell] <- mask
            meetCell cell mask 0UL |> ignore
            quiesce ()

        member _.ResetGenerated () =
            if generated.Count > 0 then
                generated.Clear()
                if baselineValid then restoreBaseline ()
                else
                    rebuild false
                    captureBaseline ()
            elif not baselineValid then
                captureBaseline ()

        member _.Subscribe handler =
            let id = nextHandler
            nextHandler <- nextHandler + 1
            handlers.[id] <- handler
            { new System.IDisposable with
                member _.Dispose () = handlers.Remove id |> ignore }



let private optimizedPremiseWidth = 64

// ---- Optimized face ------------------------------------------------------



let private finiteNet (values: 'a list) (model: Model<'a> when 'a : comparison) =
    let authored = List.toArray values
    let cells = ResizeArray<Cell<'a>>(model.cells)
    let cellPositions = Dictionary<int, int>()
    do model.cells |> List.iteri (fun index cell -> cellPositions.Add(cell.id, index))
    let constraints = ResizeArray<Constraint<'a>>(model.constraints)
    let pending = Dictionary<struct(int * int), 'a list>()
    let mutable nextPremise = 0
    let mutable engine: FiniteCore.Engine<'a> option = None
    let mutable searchPremises: int list = []

    let cellPosition cell =
        match cellPositions.TryGetValue cell.id with
        | true, index -> index
        | _ -> invalidArg "cell" "cell does not belong to this network"

    let requireAuthoring () =
        if engine.IsSome then invalidOp "structural authoring is closed after the network is first observed or run"

    let ensureEngine () =
        match engine with
        | Some found -> found
        | None ->
            let built = FiniteCore.Engine<'a>(authored, cells.ToArray(), List.ofSeq constraints)
            pending
            |> Seq.sortBy (fun pair -> let struct(premise, cell) = pair.Key in premise, cell)
            |> Seq.iter (fun pair ->
                let struct(premise, cell) = pair.Key
                built.Assert(premise, cell, pair.Value))
            pending.Clear()
            engine <- Some built
            built

    let addCell name =
        requireAuthoring ()
        let cell = Cell.create name
        let position = cells.Count
        cells.Add cell
        cellPositions.Add(cell.id, position)
        cell

    let addConstraint constraintBox =
        requireAuthoring ()
        match constraintBox with
        | Relation box when box.scopes |> Array.exists (fun cell -> not (cellPositions.ContainsKey cell.id)) ->
            invalidArg "constraintBox" "relation contains a cell outside this network"
        | Relation _ -> constraints.Add constraintBox
        | Dataflow _ -> invalidOp "optimized finite networks do not support dataflow constraints"

    let freshPremise () =
        if nextPremise >= optimizedPremiseWidth then
            invalidOp (sprintf "PremiseWidthExceeded(%d, %d)" (nextPremise + 1) optimizedPremiseWidth)
        let premise = { pid = nextPremise }
        nextPremise <- nextPremise + 1
        premise

    let assertState premise cell state =
        if premise.pid < 0 || premise.pid >= optimizedPremiseWidth then
            invalidArg "premise" "optimized premise ids must be between 0 and 63"
        nextPremise <- max nextPremise (premise.pid + 1)
        let candidates =
            match state with
            | FiniteCandidates candidates -> candidates
            | LatticeValue _ -> invalidArg "state" "finite networks require FiniteCandidates"
        let index = cellPosition cell
        match engine with
        | Some built -> built.Assert(premise.pid, index, candidates)
        | None -> pending.[struct(premise.pid, index)] <- candidates

    let retractPremise premise =
        match engine with
        | Some built -> built.Retract premise.pid
        | None ->
            let dead =
                [ for pair in pending do
                      let struct(found, _) = pair.Key
                      if found = premise.pid then yield pair.Key ]
            for key in dead do pending.Remove key |> ignore

    let clearSearch () =
        match engine with
        | Some built ->
            for premise in searchPremises |> List.sortDescending do built.Retract premise
        | None -> ()
        searchPremises <- []

    let solution (built: FiniteCore.Engine<'a>) =
        cells
        |> Seq.mapi (fun index cell -> cell, built.SingletonValue index)
        |> Map.ofSeq

    let anyBottom (built: FiniteCore.Engine<'a>) =
        cells |> Seq.mapi (fun index _ -> built.IsBottom index) |> Seq.exists id

    let run () =
        clearSearch ()
        let needed = nextPremise + cells.Count
        if needed > optimizedPremiseWidth then
            invalidOp (sprintf "PremiseWidthExceeded(%d, %d)" needed optimizedPremiseWidth)
        let built = ensureEngine ()
        built.ResetGenerated ()
        let mutable guess = nextPremise
        let rec search () =
            if anyBottom built then false
            else
                match cells |> Seq.mapi (fun index _ -> index) |> Seq.tryFind (built.IsSingleton >> not) with
                | None -> true
                | Some cell ->
                    let rec tryValues = function
                        | [] -> false
                        | value :: rest ->
                            let premise = guess
                            guess <- guess + 1
                            built.Assert(premise, cell, [ value ])
                            if search () then
                                searchPremises <- premise :: searchPremises
                                true
                            else
                                built.Retract premise
                                guess <- premise
                                tryValues rest
                    tryValues (built.Candidates cell)
        if search () then Some (solution built) else None

    let generate seed attempts =
        if attempts <= 0 then invalidArg "attempts" "generation attempts must be positive"
        clearSearch ()
        let built = ensureEngine ()
        let mutable randomState = seed
        let rec attempt number =
            built.ResetGenerated ()
            let heap = PriorityQueue<int, struct(int * int)>()
            let counts = Array.init cells.Count built.CandidateCount
            let mutable unresolved = counts |> Array.sumBy (fun count -> if count > 1 then 1 else 0)
            let mutable contradictions = counts |> Array.sumBy (fun count -> if count = 0 then 1 else 0)
            let changed cell =
                let before = counts.[cell]
                let after = built.CandidateCount cell
                counts.[cell] <- after
                if before > 1 && after <= 1 then unresolved <- unresolved - 1
                elif before <= 1 && after > 1 then unresolved <- unresolved + 1
                if before = 0 && after > 0 then contradictions <- contradictions - 1
                elif before > 0 && after = 0 then contradictions <- contradictions + 1
                if after > 1 then
                    heap.Enqueue(cell, struct(after, cell))
            for cell in 0 .. cells.Count - 1 do
                if counts.[cell] > 1 then
                    heap.Enqueue(cell, struct(counts.[cell], cell))
            use subscription = built.Subscribe changed
            let rec collapse () =
                if contradictions > 0 then false
                elif unresolved = 0 then true
                else
                    let cell = heap.Dequeue()
                    let count = counts.[cell]
                    if count <= 1 then collapse ()
                    else
                        let candidates = built.Candidates cell
                        let nextState, choice = StableRandom.bounded (uint64 count) randomState
                        randomState <- nextState
                        built.Collapse(cell, candidates.[choice])
                        collapse ()
            if collapse () then Ok (solution built)
            elif number < attempts then attempt (number + 1)
            else
                built.ResetGenerated ()
                Error (RestartLimitExceeded attempts)
        attempt 1

    let readState cell =
        let built = ensureEngine ()
        FiniteCandidates (built.Candidates (cellPosition cell))

    let readSupport cell =
        let support = (ensureEngine ()).Support(cellPosition cell)
        [ for premise in 0 .. 63 do
              if support &&& (1UL <<< premise) <> 0UL then yield { pid = premise } ]
        |> Set.ofList

    let observeCell cell handler =
        let built = ensureEngine ()
        let index = cellPosition cell
        handler (cell, FiniteCandidates (built.Candidates index))
        built.Subscribe(fun changed ->
            if changed = index then handler (cell, FiniteCandidates (built.Candidates index)))

    let observeNet handler =
        let built = ensureEngine ()
        for index in 0 .. cells.Count - 1 do
            handler (cells.[index], FiniteCandidates (built.Candidates index))
        built.Subscribe(fun changed ->
            handler (cells.[changed], FiniteCandidates (built.Candidates changed)))

    model.givens
    |> List.iter (fun (cell, value) ->
        let premise = freshPremise ()
        assertState premise cell (FiniteCandidates [ value ]))

    { addCell = addCell
      addConstraint = addConstraint
      freshPremise = freshPremise
      assertState = assertState
      retractPremise = retractPremise
      readState = readState
      readSupport = readSupport
      run = run
      generate = generate
      observeCell = observeCell
      observeNet = observeNet }

let lower (model: Model<'a> when 'a : comparison) : Result<OptimizedNet<'a>, UnsupportedConstruct list> =
    let constraintBad =
        [ for constraintBox in model.constraints do
              match constraintBox with
              | Dataflow _ -> yield DataflowOnOptimized
              | Relation _ -> () ]
    match model.domain with
    | Lattice _ -> Error (RichLatticeOnOptimized :: constraintBad)
    | Finite values ->
        let premiseBad =
            if List.length model.givens > optimizedPremiseWidth then
                [ PremiseWidthExceeded(List.length model.givens, optimizedPremiseWidth) ]
            else []
        let errors = constraintBad @ premiseBad
        if List.isEmpty errors then Ok (finiteNet values model) else Error errors

let createFinite (values: 'a list when 'a : comparison) =
    let domain = Domain.finite values
    finiteNet values { domain = domain; cells = []; constraints = []; givens = [] }

let cell (net: OptimizedNet<'a>) name = net.addCell name
let constrain (net: OptimizedNet<'a>) constraintBox = net.addConstraint constraintBox
let freshPremise (net: OptimizedNet<'a>) = net.freshPremise ()
let assertStateUnder (net: OptimizedNet<'a>) premise cell state = net.assertState premise cell state
let solve (net: OptimizedNet<'a>) = net.run ()
let value (net: OptimizedNet<'a>) cell = net.readState cell
let support (net: OptimizedNet<'a>) cell = net.readSupport cell
let solutions (net: OptimizedNet<'a>) : Solution<'a> seq = failwith "deferred: solutions"
let generateWith attempts seed (net: OptimizedNet<'a>) = net.generate seed attempts
let generate seed (net: OptimizedNet<'a>) = net.generate seed 32
let onCell (net: OptimizedNet<'a>) cell handler = net.observeCell cell handler
let onNet (net: OptimizedNet<'a>) handler = net.observeNet handler
let assume (net: OptimizedNet<'a>) cell value =
    let premise = net.freshPremise ()
    net.assertState premise cell (FiniteCandidates [ value ])
    premise
let retract (net: OptimizedNet<'a>) premise = net.retractPremise premise
