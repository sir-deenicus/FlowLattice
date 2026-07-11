#load "propagator-surface-vocab.fsx"

// Friendly syntax over the canonical propagator core. This file owns payload adaptation,
// premise names, methods, and computation-expression ergonomics only. Every cell, relation,
// assertion, retraction, read, support query, and search delegates to one live core network.

open System.Collections.Generic
open ``Propagator-surface-vocab``

type private CoreOperations<'state, 'payload> =
    { cell: string -> Cell<'state>
      convert: Cell<'state> -> Cell<'state> -> ('payload -> 'payload) -> ('payload -> 'payload) -> unit
      combine: Cell<'state> list -> Cell<'state> -> ('payload list -> 'payload) -> unit
      constrain: Constraint<'state> -> unit
      freshPremise: unit -> Premise
      assertState: Premise -> Cell<'state> -> CellState<'state> -> unit
      retract: Premise -> unit
      value: Cell<'state> -> CellState<'state>
      support: Cell<'state> -> Set<Premise>
      solve: unit -> Solution<'state> option
      generate: uint64 -> int -> Result<Solution<'state>, GenerationFailure>
      observeCell: Cell<'state> -> (Cell<'state> * CellState<'state> -> unit) -> System.IDisposable
      observeNet: (Cell<'state> * CellState<'state> -> unit) -> System.IDisposable }

/// Maps friendly payloads and returned views onto one already-created core network.
/// It contains no propagation behavior or independently mutable model state.
type PayloadAdapter<'state, 'payload, 'view> =
    private
        { operations: CoreOperations<'state, 'payload>
          read: CellState<'state> -> 'view
          assertion: 'payload -> CellState<'state>
          restriction: 'view -> CellState<'state>
          payload: 'state -> 'payload option }

/// Domain-shaped syntax over one live core network. Premise dictionaries only translate user-facing
/// names to opaque core handles and back for provenance display.
type Network<'state, 'payload, 'view when 'state : equality>(adapter: PayloadAdapter<'state, 'payload, 'view>) =
    let premiseIds = Dictionary<string, Premise>()
    let premiseNames = Dictionary<int, string>()

    let premiseId name =
        match premiseIds.TryGetValue name with
        | true, premise -> premise
        | _ ->
            let premise = adapter.operations.freshPremise ()
            premiseIds.[name] <- premise
            premiseNames.[premise.pid] <- name
            premise

    member _.Cell (name: string) = adapter.operations.cell name

    member _.Convert
        (left: Cell<'state>,
         right: Cell<'state>,
         forward: 'payload -> 'payload,
         backward: 'payload -> 'payload) =
        adapter.operations.convert left right forward backward

    member _.Combine
        (sources: Cell<'state> list,
         target: Cell<'state>,
         operation: 'payload list -> 'payload) =
        adapter.operations.combine sources target operation

    member _.Constrain (constraintBox: Constraint<'state>) =
        adapter.operations.constrain constraintBox

    member _.Relate (cells: Cell<'state> list, allows: 'state list -> bool) =
        adapter.operations.constrain (Constraint.relation cells allows)

    member _.RelateMany (scopes: seq<Cell<'state> list>, allows: 'state list -> bool) =
        adapter.operations.constrain (Constraint.relations scopes allows)

    member _.Given (cell: Cell<'state>, value: 'payload) =
        let premise = adapter.operations.freshPremise ()
        premiseNames.[premise.pid] <- sprintf "given(%s)" cell.name
        adapter.operations.assertState premise cell (adapter.assertion value)

    member _.Assume (premise: string, cell: Cell<'state>, value: 'payload) =
        adapter.operations.assertState (premiseId premise) cell (adapter.assertion value)

    member _.Restrict (cell: Cell<'state>, view: 'view) =
        let premise = adapter.operations.freshPremise ()
        premiseNames.[premise.pid] <- sprintf "restrict(%s)" cell.name
        adapter.operations.assertState premise cell (adapter.restriction view)

    member _.Restrict (premise: string, cell: Cell<'state>, view: 'view) =
        adapter.operations.assertState (premiseId premise) cell (adapter.restriction view)

    member _.Retract (premise: string) =
        match premiseIds.TryGetValue premise with
        | true, found -> adapter.operations.retract found
        | _ -> ()

    member _.Value (cell: Cell<'state>) =
        adapter.operations.value cell |> adapter.read

    member _.Support (cell: Cell<'state>) = adapter.operations.support cell

    member _.Solve () = adapter.operations.solve ()

    member _.Generate (seed: uint64) = adapter.operations.generate seed 32

    member _.Generate (seed: uint64, attempts: int) = adapter.operations.generate seed attempts

    member _.Observe (handler: Cell<'state> * CellState<'state> -> unit) =
        adapter.operations.observeNet handler

    member _.Observe (cell: Cell<'state>, handler: Cell<'state> * CellState<'state> -> unit) =
        adapter.operations.observeCell cell handler

    member _.ShowSupport (support: Set<Premise>) =
        if Set.isEmpty support then "{}"
        else
            support
            |> Seq.map (fun premise ->
                match premiseNames.TryGetValue premise.pid with
                | true, name -> name
                | _ -> sprintf "p%d" premise.pid)
            |> String.concat ", "
            |> sprintf "{%s}"

let private generalOperations
    (core: GeneralNet<'state>)
    (payload: 'state -> 'payload option)
    (inject: 'payload -> 'state) =
    { cell = General.cell core
      convert = fun left right forward backward ->
          General.convert core left right payload inject forward backward
      combine = fun sources target operation ->
          General.combine core sources target payload inject operation
      constrain = General.constrain core
      freshPremise = fun () -> General.freshPremise core
      assertState = General.assertStateUnder core
      retract = General.retract core
      value = General.value core
      support = General.support core
      solve = fun () -> General.solve core
      generate = fun seed attempts -> General.generateWith attempts seed core
      observeCell = fun cell handler -> General.onCell core cell handler
      observeNet = General.onNet core }

let private optimizedOperations (core: OptimizedNet<'state>) =
    { cell = Optimized.cell core
      convert = fun _ _ _ _ -> invalidOp "Convert requires a rich lattice domain"
      combine = fun _ _ _ -> invalidOp "Combine requires a rich lattice domain"
      constrain = Optimized.constrain core
      freshPremise = fun () -> Optimized.freshPremise core
      assertState = Optimized.assertStateUnder core
      retract = Optimized.retract core
      value = Optimized.value core
      support = Optimized.support core
      solve = fun () -> Optimized.solve core
      generate = fun seed attempts -> Optimized.generateWith attempts seed core
      observeCell = fun cell handler -> Optimized.onCell core cell handler
      observeNet = Optimized.onNet core }

module Domain =

    let scalar<'value when 'value : equality> () : Network<Scalar<'value>, 'value, Scalar<'value>> =
        let domain =
            ``Propagator-surface-vocab``.Domain.lattice
                Top scalarMeet (function Bot -> true | _ -> false)
        let core = General.createLattice domain
        Network
            { operations = generalOperations core (function Val value -> Some value | _ -> None) Val
              read = function LatticeValue value -> value | _ -> invalidOp "scalar state mismatch"
              assertion = Val >> LatticeValue
              restriction = LatticeValue
              payload = function Val value -> Some value | _ -> None }

    let interval () : Network<Interval, Interval, Interval> =
        let domain =
            ``Propagator-surface-vocab``.Domain.lattice
                Interval.entire Interval.meet (function Empty -> true | _ -> false)
        let core = General.createLattice domain
        Network
            { operations = generalOperations core (function Empty -> None | interval -> Some interval) id
              read = function LatticeValue value -> value | _ -> invalidOp "interval state mismatch"
              assertion = LatticeValue
              restriction = LatticeValue
              payload = function Empty -> None | interval -> Some interval }

    let fixedPoint (quantum: decimal) : Network<Interval, FixedPoint, FixedPoint> =
        FixedPoint(0m, quantum) |> ignore
        let wrap interval = FixedPoint.FromInterval(interval, quantum)
        let project = function Empty -> None | interval -> Some (wrap interval)
        let inject (value: FixedPoint) = value.Interval
        let domain =
            ``Propagator-surface-vocab``.Domain.lattice
                Interval.entire Interval.meet (function Empty -> true | _ -> false)
        let core = General.createLattice domain
        Network
            { operations = generalOperations core project inject
              read = function LatticeValue value -> wrap value | _ -> invalidOp "fixed-point state mismatch"
              assertion = inject >> LatticeValue
              restriction = inject >> LatticeValue
              payload = project }

    let finite (values: 'value list) : Network<'value, 'value, Set<'value>> when 'value : comparison =
        let core = Optimized.createFinite values
        Network
            { operations = optimizedOperations core
              read = function
                  | FiniteCandidates candidates -> Set.ofList candidates
                  | _ -> invalidOp "finite state mismatch"
              assertion = fun value -> FiniteCandidates [ value ]
              restriction = fun allowed ->
                  FiniteCandidates
                      [ for value in values do
                            if Set.contains value allowed then yield value ]
              payload = Some }

type NetworkBuilder<'state, 'payload, 'view when 'state : equality>(network: Network<'state, 'payload, 'view>) =
    member _.Bind
        (operation: Network<'state, 'payload, 'view> -> 'result,
         next: 'result -> Network<'state, 'payload, 'view> -> 'next) =
        fun current -> next (operation current) current
    member _.Return value = fun (_: Network<'state, 'payload, 'view>) -> value
    member _.ReturnFrom operation = operation
    member _.Zero () = fun (_: Network<'state, 'payload, 'view>) -> ()
    member _.Combine (first, second) = fun current -> first current; second current
    member _.Delay delayed = fun current -> delayed () current
    member _.For (items: seq<'item>, body) =
        fun current -> for item in items do body item current
    member _.Run operation = operation network

let network (value: Network<'state, 'payload, 'view>) = NetworkBuilder(value)

module Ops =
    let cell name (network: Network<'state, 'payload, 'view>) = network.Cell name
    let convert left right forward backward (network: Network<'state, 'payload, 'view>) =
        network.Convert(left, right, forward, backward)
    let combine sources target operation (network: Network<'state, 'payload, 'view>) =
        network.Combine(sources, target, operation)
    let constrain constraintBox (network: Network<'state, 'payload, 'view>) = network.Constrain constraintBox
    let relate cells allows (network: Network<'state, 'payload, 'view>) = network.Relate(cells, allows)
    let relateMany scopes allows (network: Network<'state, 'payload, 'view>) = network.RelateMany(scopes, allows)
    let assume premise cell value (network: Network<'state, 'payload, 'view>) = network.Assume(premise, cell, value)
    let given cell value (network: Network<'state, 'payload, 'view>) = network.Given(cell, value)
    let restrict cell view (network: Network<'state, 'payload, 'view>) = network.Restrict(cell, view)
    let restrictNamed premise cell view (network: Network<'state, 'payload, 'view>) =
        network.Restrict(premise, cell, view)
    let retract premise (network: Network<'state, 'payload, 'view>) = network.Retract premise
    let read cell (network: Network<'state, 'payload, 'view>) = network.Value cell
    let solve (network: Network<'state, 'payload, 'view>) = network.Solve ()
    let generate seed (network: Network<'state, 'payload, 'view>) = network.Generate seed
    let generateWith attempts seed (network: Network<'state, 'payload, 'view>) = network.Generate(seed, attempts)
    let observe handler (network: Network<'state, 'payload, 'view>) = network.Observe handler
    let observeCell cell handler (network: Network<'state, 'payload, 'view>) = network.Observe(cell, handler)
