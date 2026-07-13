module Propagator.General

open System.Collections.Generic
open Propagator.Core

/// A lowered GENERAL network retaining one live closure engine. Ordinary factories auto-quiesce;
/// batched factories keep authoring, edits, reads, and support inspection passive until `propagate`.
type GeneralNet<'a> =
    private
        { domain: Domain<'a>
          addCell: string -> Cell<'a>
          addConstraint: Constraint<'a> -> unit
          freshPremise: unit -> Premise
          assertState: Premise -> Cell<'a> -> CellState<'a> -> unit
          retractPremise: Premise -> unit
          readState: Cell<'a> -> CellState<'a>
          readSupport: Cell<'a> -> Set<Premise>
          readEvidence: Cell<'a> -> Support
          propagate: int -> PropagationResult<'a>
          run: unit -> Solution<'a> option
          generate: uint64 -> int -> Result<Solution<'a>, GenerationFailure>
          observeCell: Cell<'a> -> (Cell<'a> * CellState<'a> -> unit) -> System.IDisposable
          observeNet: (Cell<'a> * CellState<'a> -> unit) -> System.IDisposable }


// ---- Backend engines (the two lowering targets, copied and adapted) -----
// Their internal `Cell` differs from the public handle above, so each is nested and private.

/// The GENERAL backend: the closure `Engine<'a>` of tutorial-propagation-part1.fsx (its 7a-7e),
/// with integration deltas for lattice-supplied equality/bottom, structural provenance, and the
/// counted staged pump. Clear rep, closure props, premises + Retract.
module private Closure =

    type Premise = int
    type Origin = Ext of Premise | Prop of int | Generated of int
    type Provenance =
        { premises: Set<Premise>
          constraints: Set<ConstraintId> }

    let emptyProvenance =
        { premises = Set.empty
          constraints = Set.empty }

    let unionTwo left right =
        { premises = Set.union left.premises right.premises
          constraints =
            if Set.isEmpty left.constraints then right.constraints
            elif Set.isEmpty right.constraints then left.constraints
            else Set.union left.constraints right.constraints }

    let unionProvenance (supports: seq<Provenance>) =
        supports |> Seq.fold unionTwo emptyProvenance

    type Contribution<'a> = { value: 'a; support: Provenance }

    type Cell<'a> =
        { id: int
          orderKey: string
          contribs: Dictionary<Origin, Contribution<'a>>
          mutable value: 'a
          mutable support: Provenance }

    type Lattice<'a> =
        { top: 'a
          meet: 'a -> 'a -> 'a
          equals: 'a -> 'a -> bool
          isBottom: 'a -> bool }

    type Propagator<'a> =
        { pid: int
          reads: Cell<'a> list
          constraintId: ConstraintId option
          orderKey: string option
          mutable initialized: bool
          fire: unit -> (Cell<'a> * Contribution<'a>) list }

    type private Work<'a> =
        | Recompute of Cell<'a> * wakeWhenUnchanged: bool
        | Fire of Propagator<'a>

    type PumpResult =
        | Settled of narrowingEvents: int
        | ReachedBottom of support: Provenance * narrowingEvents: int
        | Limited of narrowingEvents: int

    type Engine<'a>(L: Lattice<'a>) =
        let cells = ResizeArray<Cell<'a>>()
        let watch = Dictionary<int, ResizeArray<Propagator<'a>>>()
        let mutable nCell = 0
        let mutable nProp = 0
        let handlers = Dictionary<int, int * 'a -> unit>()
        let mutable nHandler = 0
        let pending = Queue<Work<'a>>()
        let mutable boundedPropagationActive = false

        member private _.Recompute (c: Cell<'a>) =
            let before = c.value
            let mutable v = L.top
            let mutable s = emptyProvenance
            for kv in c.contribs do
                v <- L.meet v kv.Value.value
                if not (L.equals kv.Value.value L.top) then
                    s <- unionTwo s kv.Value.support
            c.value <- v; c.support <- s
            let changed = not (L.equals before v)
            if changed then
                for handler in handlers.Values do handler (c.id, v)
            changed, L.isBottom v, s

        member private this.Pump
            (maxNarrowingEvents: int, stopOnBottom: bool, canonicalSchedule: bool) =
            if maxNarrowingEvents < 0 then
                invalidArg "maxNarrowingEvents" "propagation budgets must be non-negative"

            let work = Queue<Work<'a>>()
            let propagatorKey (propagator: Propagator<'a>) =
                match propagator.orderKey with
                | Some key -> key
                | None -> sprintf "~%010d" propagator.pid
            let workKey = function
                | Fire propagator -> 0, propagatorKey propagator
                | Recompute(cell, _) -> 1, cell.orderKey
            let wake (c: Cell<'a>) =
                let propagators = watch.[c.id]
                if canonicalSchedule then
                    propagators
                    |> Seq.sortBy propagatorKey
                    |> Seq.iter (Fire >> work.Enqueue)
                else
                    for propagator in propagators do work.Enqueue(Fire propagator)
            let drainPending () =
                if canonicalSchedule && pending.Count > 1 then
                    let staged = [| while pending.Count > 0 do yield pending.Dequeue() |]
                    Array.sortInPlaceBy workKey staged
                    for item in staged do work.Enqueue item
                else
                    while pending.Count > 0 do work.Enqueue(pending.Dequeue())
            let terminal narrowingEvents =
                match cells |> Seq.tryFind (fun cell -> L.isBottom cell.value) with
                | Some cell -> ReachedBottom(cell.support, narrowingEvents)
                | None -> Settled narrowingEvents
            let mutable narrowingEvents = 0
            let mutable outcome: PumpResult option = None
            let retainWork interrupted =
                let retained = ResizeArray<Work<'a>>()
                interrupted |> Option.iter (fun item -> retained.Add item)
                while work.Count > 0 do retained.Add(work.Dequeue())
                while pending.Count > 0 do retained.Add(pending.Dequeue())
                for item in retained do pending.Enqueue item
            let stopEarly interrupted result =
                retainWork interrupted
                outcome <- Some result
            let stopAtLimit interrupted narrowingEvents =
                if Option.isNone interrupted && work.Count = 0 && pending.Count = 0 then
                    outcome <- Some (terminal narrowingEvents)
                else
                    let result =
                        match cells |> Seq.tryFind (fun cell -> L.isBottom cell.value) with
                        | Some cell -> ReachedBottom(cell.support, narrowingEvents)
                        | None -> Limited narrowingEvents
                    stopEarly interrupted result

            drainPending ()

            while outcome.IsNone do
                drainPending ()

                if work.Count = 0 then
                    outcome <- Some (terminal narrowingEvents)
                elif narrowingEvents = maxNarrowingEvents then
                    stopAtLimit None narrowingEvents
                else
                    match work.Dequeue() with
                    | Recompute(cell, wakeWhenUnchanged) ->
                        let changed, bottom, support = this.Recompute cell
                        if changed then
                            narrowingEvents <- narrowingEvents + 1
                            wake cell
                            if bottom && stopOnBottom then
                                stopEarly None (ReachedBottom(support, narrowingEvents))
                            elif narrowingEvents = maxNarrowingEvents then
                                stopAtLimit None narrowingEvents
                        elif wakeWhenUnchanged then
                            wake cell
                    | Fire propagator ->
                        propagator.initialized <- true
                        let mutable emitted = propagator.fire ()
                        while outcome.IsNone && not (List.isEmpty emitted) do
                            let target, fact = List.head emitted
                            emitted <- List.tail emitted
                            let support =
                                match propagator.constraintId with
                                | Some constraintId when not (L.equals fact.value L.top) ->
                                    { fact.support with
                                        constraints = Set.add constraintId fact.support.constraints }
                                | _ -> fact.support
                            target.contribs.[Prop propagator.pid] <- { fact with support = support }
                            let changed, bottom, support = this.Recompute target
                            if changed then
                                narrowingEvents <- narrowingEvents + 1
                                wake target
                                let interrupted =
                                    if List.isEmpty emitted then None
                                    else Some (Fire propagator)
                                if bottom && stopOnBottom then
                                    stopEarly interrupted (ReachedBottom(support, narrowingEvents))
                                elif narrowingEvents = maxNarrowingEvents then
                                    stopAtLimit interrupted narrowingEvents
            outcome.Value

        member _.NewCell (orderKey: string) =
            let c =
                { id = nCell
                  orderKey = orderKey
                  contribs = Dictionary()
                  value = L.top
                  support = emptyProvenance }
            nCell <- nCell + 1; watch.[c.id] <- ResizeArray(); cells.Add c; c

        member _.AddProp
            (reads: Cell<'a> list,
             constraintId: ConstraintId option,
             fire: unit -> (Cell<'a> * Contribution<'a>) list) =
            let orderKey =
                constraintId
                |> Option.map (fun id ->
                    sprintf "%s|%s"
                        (ConstraintId.value id)
                        (reads |> Seq.map (fun cell -> cell.orderKey) |> String.concat "|"))
            let p =
                { pid = nProp
                  reads = reads
                  constraintId = constraintId
                  orderKey = orderKey
                  initialized = false
                  fire = fire }
            nProp <- nProp + 1
            for c in reads do watch.[c.id].Add p
            p

        member _.StageProp (propagator: Propagator<'a>) =
            pending.Enqueue(Fire propagator)

        member private this.QuiescePending () =
            match this.Pump(System.Int32.MaxValue, false, false) with
            | Limited _ -> invalidOp "automatic propagation exceeded its event capacity"
            | Settled _ | ReachedBottom _ -> ()

        member this.Stabilize () =
            for propagators in watch.Values do
                for propagator in propagators do
                    if not propagator.initialized then
                        propagator.initialized <- true
                        pending.Enqueue(Fire propagator)
            this.QuiescePending ()

        member this.Propagate (maxNarrowingEvents: int) =
            if boundedPropagationActive then
                invalidOp "A batched network cannot re-enter General.propagate from an observer handler"
            boundedPropagationActive <- true
            try
                this.Pump(maxNarrowingEvents, true, true)
            finally
                boundedPropagationActive <- false

        member _.StageAssert (p: Premise, c: Cell<'a>, v: 'a) =
            c.contribs.[Ext p] <-
                { value = v
                  support =
                    { premises = Set.singleton p
                      constraints = Set.empty } }
            pending.Enqueue(Recompute(c, true))

        member _.StageRetract (p: Premise) =
            for c in cells do
                let dead =
                    [ for kv in c.contribs do
                          if kv.Value.support.premises.Contains p then yield kv.Key ]
                if not dead.IsEmpty then
                    for key in dead do c.contribs.Remove key |> ignore
                    pending.Enqueue(Recompute(c, true))

        member this.Collapse (c: Cell<'a>, value: 'a) =
            c.contribs.[Generated c.id] <- { value = value; support = emptyProvenance }
            pending.Enqueue(Recompute(c, true))
            this.QuiescePending ()

        member this.ResetGenerated () =
            for c in cells do
                let dead =
                    [ for kv in c.contribs do
                          match kv.Key with
                          | Prop _ | Generated _ -> yield kv.Key
                          | Ext _ -> () ]
                for key in dead do c.contribs.Remove key |> ignore
                pending.Enqueue(Recompute(c, true))
            this.QuiescePending ()

        member _.Subscribe (handler: int * 'a -> unit) =
            let id = nHandler
            nHandler <- nHandler + 1
            handlers.[id] <- handler
            { new System.IDisposable with
                member _.Dispose () = handlers.Remove id |> ignore }

        member _.Value (c: Cell<'a>) = c.value

        member this.Assert (p: Premise, c: Cell<'a>, v: 'a) =
            this.StageAssert(p, c, v)
            this.QuiescePending ()

        member this.Retract (p: Premise) =
            this.StageRetract p
            this.QuiescePending ()


// ---- General face -------------------------------------------------------



let private unsupported (model: Model<'a>) =
    [ for constraintBox in model.constraints do
          match model.domain, constraintBox with
          | Finite _, Dataflow _ -> yield DataflowRequiresRichLattice
          | Lattice _, Relation _ -> yield RelationRequiresFiniteDomain
          | _ -> () ]

let private publicEvidence (support: Closure.Provenance) : Support =
    { Premises = support.premises |> Seq.map (fun pid -> { pid = pid }) |> Set.ofSeq
      Constraints = support.constraints }

let private publicSupport support = (publicEvidence support).Premises

let private finiteNet
    (rep: FiniteRep<'a, 'state>)
    (model: Model<'a> when 'a : comparison)
    (automatic: bool)
    : GeneralNet<'a> =
    let lattice : Closure.Lattice<'state> =
        { top = rep.top
          meet = rep.meet
          equals = rep.equals
          isBottom = rep.isBottom }
    let eng = Closure.Engine<'state>(lattice)
    let emap = Dictionary<int, Closure.Cell<'state>>()
    let cells = ResizeArray<Cell<'a>>()
    let authoredValues = match model.domain with Finite values -> values | _ -> []
    let allowed = HashSet<'a>(authoredValues)
    let mutable nextPremise = 0

    let addExisting (cell: Cell<'a>) =
        if emap.ContainsKey cell.id then invalidArg "cell" "cell is already part of this network"
        emap.[cell.id] <- eng.NewCell(sprintf "%s|%010d" cell.name cell.id)
        cells.Add cell

    let ecell (cell: Cell<'a>) =
        match emap.TryGetValue cell.id with
        | true, found -> found
        | _ -> invalidArg "cell" "cell does not belong to this network"

    let addCell name =
        let cell = Cell.create name
        addExisting cell
        cell

    let addConstraint constraintBox =
        match constraintBox with
        | Relation box ->
            for offset in 0 .. box.arity .. box.scopes.Length - 1 do
                let engineCells =
                    [ for index in offset .. offset + box.arity - 1 -> ecell box.scopes.[index] ]
                let propagator =
                    eng.AddProp(engineCells, box.constraintId, fun () ->
                        let candidates = engineCells |> List.map (fun cell -> rep.candidates cell.value)
                        let narrowed = Gac.narrow box.allows candidates
                        let support =
                            engineCells
                            |> List.map (fun cell -> cell.support)
                            |> Closure.unionProvenance
                        List.map2 (fun (cell: Closure.Cell<'state>) values ->
                            cell,
                            { Closure.value = rep.ofValues values
                              Closure.support = support }) engineCells narrowed)
                if not automatic then eng.StageProp propagator
        | Dataflow _ -> invalidOp "Dataflow constraints require a rich lattice domain"

    let freshPremise () =
        let premise = { pid = nextPremise }
        nextPremise <- nextPremise + 1
        premise

    let assertState premise cell state =
        if premise.pid < 0 then invalidArg "premise" "premise ids must be non-negative"
        nextPremise <- max nextPremise (premise.pid + 1)
        match state with
        | FiniteCandidates values ->
            if values |> List.exists (allowed.Contains >> not) then
                invalidArg "state" "candidate is not in the authored finite domain"
            if automatic then eng.Assert(premise.pid, ecell cell, rep.ofValues values)
            else eng.StageAssert(premise.pid, ecell cell, rep.ofValues values)
        | LatticeValue _ -> invalidArg "state" "finite networks require FiniteCandidates"

    let engineCells () = cells |> Seq.map (fun cell -> cell, ecell cell) |> Seq.toList
    let settle () = if automatic then eng.Stabilize ()
    let snapshot () =
        engineCells ()
        |> List.sortBy (fun (cell, _) -> cell.name, cell.id)
        |> List.map (fun (cell, engineCell) ->
            { cell = cell
              state = FiniteCandidates (rep.candidates engineCell.value)
              support = publicEvidence engineCell.support })
    let propagate maxNarrowingEvents =
        if automatic then
            invalidOp "Explicit propagation requires a network built with a General batched factory"
        match eng.Propagate maxNarrowingEvents with
        | Closure.Settled events -> Fixpoint(snapshot (), events)
        | Closure.ReachedBottom(support, events) ->
            Contradiction(snapshot (), publicEvidence support, events)
        | Closure.Limited events -> Truncated(snapshot (), events)
    let solution () =
        engineCells ()
        |> List.map (fun (publicCell, engineCell) ->
            publicCell, List.head (rep.candidates engineCell.value))
        |> Map.ofList

    let run () =
        if not automatic then
            invalidOp "Solve is unavailable on a batched network; call General.propagate explicitly"
        eng.Stabilize ()
        let engineCells = engineCells ()
        let anyBottom () = engineCells |> List.exists (fun (_, cell) -> rep.isBottom cell.value)
        let rec search () =
            if anyBottom () then false
            else
                match engineCells |> List.tryFind (fun (_, cell) ->
                          not (rep.isBottom cell.value) && not (rep.isSingleton cell.value)) with
                | None -> true
                | Some (_, cell) ->
                    let rec tryValues = function
                        | [] -> false
                        | value :: rest ->
                            let premise = nextPremise
                            nextPremise <- premise + 1
                            eng.Assert(premise, cell, rep.singleton value)
                            if search () then true
                            else
                                eng.Retract premise
                                nextPremise <- premise
                                tryValues rest
                    tryValues (rep.candidates cell.value)
        if search () then
            Some (solution ())
        else
            None

    let generate seed attempts =
        if not automatic then
            invalidOp "Generate is unavailable on a batched network; call General.propagate explicitly"
        if attempts <= 0 then invalidArg "attempts" "generation attempts must be positive"
        let mutable randomState = seed
        let rec attempt number =
            eng.ResetGenerated ()
            let rec collapse () =
                let openCell =
                    engineCells ()
                    |> List.choose (fun (_, cell) ->
                        let candidates = rep.candidates cell.value
                        if List.length candidates > 1 then Some (List.length candidates, cell, candidates)
                        else None)
                    |> List.sortBy (fun (count, cell, _) -> count, cell.id)
                    |> List.tryHead
                if engineCells () |> List.exists (fun (_, cell) -> rep.isBottom cell.value) then false
                else
                    match openCell with
                    | None -> true
                    | Some (count, cell, candidates) ->
                        let nextState, choice = StableRandom.bounded (uint64 count) randomState
                        randomState <- nextState
                        eng.Collapse(cell, rep.singleton candidates.[choice])
                        collapse ()
            if collapse () then Ok (solution ())
            elif number < attempts then attempt (number + 1)
            else
                eng.ResetGenerated ()
                Error (RestartLimitExceeded attempts)
        attempt 1

    let observeCell cell handler =
        settle ()
        let engineCell = ecell cell
        handler (cell, FiniteCandidates (rep.candidates engineCell.value))
        eng.Subscribe(fun (changed, state) ->
            if changed = engineCell.id then handler (cell, FiniteCandidates (rep.candidates state)))

    let observeNet handler =
        settle ()
        for cell, engineCell in engineCells () do
            handler (cell, FiniteCandidates (rep.candidates engineCell.value))
        let byId = engineCells () |> Seq.map (fun (cell, engineCell) -> engineCell.id, cell) |> dict
        eng.Subscribe(fun (changed, state) ->
            handler (byId.[changed], FiniteCandidates (rep.candidates state)))

    model.cells |> List.iter addExisting
    model.constraints |> List.iter addConstraint
    model.givens
    |> List.iter (fun (cell, value) ->
        let premise = freshPremise ()
        assertState premise cell (FiniteCandidates [ value ]))

    { domain = model.domain
      addCell = addCell
      addConstraint = addConstraint
      freshPremise = freshPremise
      assertState = assertState
      retractPremise = fun premise ->
          if automatic then eng.Retract premise.pid else eng.StageRetract premise.pid
      readState = fun cell -> settle (); FiniteCandidates (rep.candidates (ecell cell).value)
      readSupport = fun cell -> settle (); publicSupport (ecell cell).support
      readEvidence = fun cell -> settle (); publicEvidence (ecell cell).support
      propagate = propagate
      run = run
      generate = generate
      observeCell = observeCell
      observeNet = observeNet }

let private latticeNet (ops: LatticeOps<'a>) (model: Model<'a>) (automatic: bool) : GeneralNet<'a> =
    let lattice : Closure.Lattice<'a> =
        { top = ops.top
          meet = ops.meet
          equals = ops.equals
          isBottom = ops.isBottom }
    let eng = Closure.Engine<'a>(lattice)
    let emap = Dictionary<int, Closure.Cell<'a>>()
    let cells = ResizeArray<Cell<'a>>()
    let mutable nextPremise = 0

    let addExisting (cell: Cell<'a>) =
        if emap.ContainsKey cell.id then invalidArg "cell" "cell is already part of this network"
        emap.[cell.id] <- eng.NewCell(sprintf "%s|%010d" cell.name cell.id)
        cells.Add cell

    let ecell (cell: Cell<'a>) =
        match emap.TryGetValue cell.id with
        | true, found -> found
        | _ -> invalidArg "cell" "cell does not belong to this network"

    let addCell name =
        let cell = Cell.create name
        addExisting cell
        cell

    let addConstraint constraintBox =
        match constraintBox with
        | Dataflow box ->
            let reads = box.cells |> List.map ecell
            let outputs = box.outputs |> List.map ecell
            let propagator =
                eng.AddProp(reads, box.constraintId, fun () ->
                    let values = reads |> List.map (fun cell -> cell.value)
                    let derived = box.narrow values
                    if List.length derived <> List.length outputs then
                        invalidOp "dataflow output count does not match its target count"
                    let support = reads |> List.map (fun cell -> cell.support) |> Closure.unionProvenance
                    List.zip outputs derived
                    |> List.choose (fun (cell, value) ->
                        value
                        |> Option.map (fun narrowed ->
                            cell, { Closure.value = narrowed; Closure.support = support })))
            if not automatic then eng.StageProp propagator
        | Relation _ -> invalidOp "Relation constraints require a finite domain"

    let freshPremise () =
        let premise = { pid = nextPremise }
        nextPremise <- nextPremise + 1
        premise

    let assertState premise cell state =
        if premise.pid < 0 then invalidArg "premise" "premise ids must be non-negative"
        nextPremise <- max nextPremise (premise.pid + 1)
        match state with
        | LatticeValue value ->
            if automatic then eng.Assert(premise.pid, ecell cell, value)
            else eng.StageAssert(premise.pid, ecell cell, value)
        | FiniteCandidates _ -> invalidArg "state" "rich lattice networks require LatticeValue"

    let settle () = if automatic then eng.Stabilize ()
    let snapshot () =
        cells
        |> Seq.map (fun cell -> cell, ecell cell)
        |> Seq.sortBy (fun (cell, _) -> cell.name, cell.id)
        |> Seq.map (fun (cell, engineCell) ->
            { cell = cell
              state = LatticeValue engineCell.value
              support = publicEvidence engineCell.support })
        |> Seq.toList
    let propagate maxNarrowingEvents =
        if automatic then
            invalidOp "Explicit propagation requires a network built with a General batched factory"
        match eng.Propagate maxNarrowingEvents with
        | Closure.Settled events -> Fixpoint(snapshot (), events)
        | Closure.ReachedBottom(support, events) ->
            Contradiction(snapshot (), publicEvidence support, events)
        | Closure.Limited events -> Truncated(snapshot (), events)

    let run () =
        if not automatic then
            invalidOp "Solve is unavailable on a batched network; call General.propagate explicitly"
        eng.Stabilize ()
        let settled = cells |> Seq.map (fun cell -> cell, (ecell cell).value) |> Seq.toList
        if settled |> List.exists (fun (_, value) -> ops.isBottom value) then None
        else Some (Map.ofList settled)

    let observeCell cell handler =
        settle ()
        let engineCell = ecell cell
        handler (cell, LatticeValue engineCell.value)
        eng.Subscribe(fun (changed, state) ->
            if changed = engineCell.id then handler (cell, LatticeValue state))

    let observeNet handler =
        settle ()
        let engineCells = cells |> Seq.map (fun cell -> cell, ecell cell) |> Seq.toArray
        for cell, engineCell in engineCells do handler (cell, LatticeValue engineCell.value)
        let byId = engineCells |> Seq.map (fun (cell, engineCell) -> engineCell.id, cell) |> dict
        eng.Subscribe(fun (changed, state) -> handler (byId.[changed], LatticeValue state))

    model.cells |> List.iter addExisting
    model.constraints |> List.iter addConstraint
    model.givens
    |> List.iter (fun (cell, value) ->
        let premise = freshPremise ()
        assertState premise cell (LatticeValue value))

    { domain = model.domain
      addCell = addCell
      addConstraint = addConstraint
      freshPremise = freshPremise
      assertState = assertState
      retractPremise = fun premise ->
          if automatic then eng.Retract premise.pid else eng.StageRetract premise.pid
      readState = fun cell -> settle (); LatticeValue (eng.Value (ecell cell))
      readSupport = fun cell -> settle (); publicSupport (ecell cell).support
      readEvidence = fun cell -> settle (); publicEvidence (ecell cell).support
      propagate = propagate
      run = run
      generate = fun _ _ -> invalidOp "Generate requires a finite domain"
      observeCell = observeCell
      observeNet = observeNet }

let private lowerWithBehavior
    automatic
    (makeFiniteRep: 'a list -> FiniteRep<'a, 'state>)
    (model: Model<'a> when 'a : comparison)
    : Result<GeneralNet<'a>, UnsupportedConstruct list> =
    let errors = unsupported model
    if not (List.isEmpty errors) then Error errors
    else
        match model.domain with
        | Finite values -> Ok (finiteNet (makeFiniteRep values) model automatic)
        | Lattice ops -> Ok (latticeNet ops model automatic)

/// Lower with a caller-supplied finite representation. The representation state remains internal
/// to the captured engine and does not appear in the returned net, cell state, or solution.
let lowerWith
    (makeFiniteRep: 'a list -> FiniteRep<'a, 'state>)
    (model: Model<'a> when 'a : comparison)
    : Result<GeneralNet<'a>, UnsupportedConstruct list> =
    lowerWithBehavior true makeFiniteRep model

/// Lower without implicit closure. Authoring, assertions, reads, and support inspection remain
/// passive until `propagate` runs the explicitly bounded accounting window.
let lowerBatchedWith
    (makeFiniteRep: 'a list -> FiniteRep<'a, 'state>)
    (model: Model<'a> when 'a : comparison)
    : Result<GeneralNet<'a>, UnsupportedConstruct list> =
    lowerWithBehavior false makeFiniteRep model

/// Friendly finite-domain default. All lowering logic remains in `lowerWith`; this wrapper only
/// selects the Set adapter.
let lower (model: Model<'a> when 'a : comparison) : Result<GeneralNet<'a>, UnsupportedConstruct list> =
    lowerWith FiniteRep.set model

let lowerBatched
    (model: Model<'a> when 'a : comparison)
    : Result<GeneralNet<'a>, UnsupportedConstruct list> =
    lowerBatchedWith FiniteRep.set model

/// Start an empty live general network. Added cells and constraints are installed directly in
/// its retained closure engine.
let create (domain: Domain<'a> when 'a : comparison) : GeneralNet<'a> =
    let model = { domain = domain; cells = []; constraints = []; givens = [] }
    match domain with
    | Finite values -> finiteNet (FiniteRep.set values) model true
    | Lattice ops -> latticeNet ops model true

/// Start an empty network whose closure advances only through an explicit bounded `propagate` call.
let createBatched (domain: Domain<'a> when 'a : comparison) : GeneralNet<'a> =
    let model = { domain = domain; cells = []; constraints = []; givens = [] }
    match domain with
    | Finite values -> finiteNet (FiniteRep.set values) model false
    | Lattice ops -> latticeNet ops model false

/// Start an empty live rich-lattice network without imposing a comparison constraint on its values.
let createLattice (domain: Domain<'a>) : GeneralNet<'a> =
    match domain with
    | Lattice ops ->
        latticeNet ops { domain = domain; cells = []; constraints = []; givens = [] } true
    | Finite _ -> invalidArg "domain" "createLattice requires a rich lattice domain"

let createBatchedLattice (domain: Domain<'a>) : GeneralNet<'a> =
    match domain with
    | Lattice ops ->
        latticeNet ops { domain = domain; cells = []; constraints = []; givens = [] } false
    | Finite _ -> invalidArg "domain" "createBatchedLattice requires a rich lattice domain"

/// Start an empty live finite network using the friendly Set representation default.
let createFinite (values: 'a list when 'a : comparison) : GeneralNet<'a> =
    let domain = Domain.finite values
    finiteNet (FiniteRep.set values) { domain = domain; cells = []; constraints = []; givens = [] } true

let createBatchedFinite (values: 'a list when 'a : comparison) : GeneralNet<'a> =
    let domain = Domain.finite values
    finiteNet (FiniteRep.set values) { domain = domain; cells = []; constraints = []; givens = [] } false

let cell (net: GeneralNet<'a>) (name: string) : Cell<'a> = net.addCell name

let constrain (net: GeneralNet<'a>) (constraintBox: Constraint<'a>) : unit =
    net.addConstraint constraintBox

let constrainNamed
    (net: GeneralNet<'a>)
    (constraintId: ConstraintId)
    (constraintBox: Constraint<'a>)
    : unit =
    net.addConstraint (Constraint.named constraintId constraintBox)

let convert (net: GeneralNet<'a>) left right payload inject forward backward =
    Constraint.convert left right payload inject forward backward
    |> List.iter net.addConstraint

let convertNamed (net: GeneralNet<'a>) constraintId left right payload inject forward backward =
    Constraint.convert left right payload inject forward backward
    |> List.map (Constraint.named constraintId)
    |> List.iter net.addConstraint

let combine (net: GeneralNet<'a>) sources target payload inject operation =
    Constraint.combine sources target payload inject operation
    |> net.addConstraint

let combineNamed (net: GeneralNet<'a>) constraintId sources target payload inject operation =
    Constraint.combine sources target payload inject operation
    |> Constraint.named constraintId
    |> net.addConstraint

let freshPremise (net: GeneralNet<'a>) : Premise = net.freshPremise ()

let assertStateUnder
    (net: GeneralNet<'a>)
    (premise: Premise)
    (cell: Cell<'a>)
    (state: CellState<'a>)
    : unit =
    net.assertState premise cell state

let assertUnder (net: GeneralNet<'a>) (premise: Premise) (cell: Cell<'a>) (value: 'a) : unit =
    match net.domain with
    | Finite _ -> net.assertState premise cell (FiniteCandidates [ value ])
    | Lattice _ -> net.assertState premise cell (LatticeValue value)

/// One solution (DDB on the closure engine: guess = Assert under a fresh premise, backtrack = Retract).
let solve (net: GeneralNet<'a>) : Solution<'a> option = net.run ()

let solveFinite (net: GeneralNet<'a>) : bool =
    match net.domain with
    | Finite _ -> net.run () |> Option.isSome
    | Lattice _ -> invalidOp "Solve requires a finite domain (build the network with Domain.finite)"

let value (net: GeneralNet<'a>) (cell: Cell<'a>) : CellState<'a> = net.readState cell

let support (net: GeneralNet<'a>) (cell: Cell<'a>) : Set<Premise> = net.readSupport cell

let evidence (net: GeneralNet<'a>) (cell: Cell<'a>) : Support = net.readEvidence cell

/// Advance an explicitly batched network by at most the permitted aggregate changes. Work left by
/// truncation or contradiction remains staged for a later call. Observer handlers cannot recursively
/// call `propagate` on the same network while its current bounded window is active.
let propagate maxNarrowingEvents (net: GeneralNet<'a>) : PropagationResult<'a> =
    net.propagate maxNarrowingEvents

// ---- Observation / edit seams — the deferred observation slice, still stubbed (docs §9, §12) ----

let solutions (net: GeneralNet<'a>) : Solution<'a> seq = failwith "deferred: solutions"
let generateWith attempts seed (net: GeneralNet<'a>) = net.generate seed attempts
let generate seed (net: GeneralNet<'a>) = net.generate seed 32
let onCell (net: GeneralNet<'a>) (cell: Cell<'a>) handler = net.observeCell cell handler
let onNet (net: GeneralNet<'a>) handler = net.observeNet handler
let assume (net: GeneralNet<'a>) (cell: Cell<'a>) (value: 'a) : Premise =
    let premise = net.freshPremise ()
    assertUnder net premise cell value
    premise
let retract (net: GeneralNet<'a>) (premise: Premise) : unit =
    net.retractPremise premise
