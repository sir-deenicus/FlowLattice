#load "propagator-surface-vocab.fsx"

// Friendly syntax over the canonical propagator core. This file owns payload adaptation,
// premise names, methods, and computation-expression ergonomics only. Every cell, relation,
// assertion, retraction, read, support query, and search delegates to General's live core net.

open System.Collections.Generic
open ``Propagator-surface-vocab``

/// Maps friendly payloads and returned views onto one already-created core network.
/// It contains no propagation behavior or lifecycle state.
type PayloadAdapter<'state, 'payload, 'view> =
    private
        { core: GeneralNet<'state>
          read: CellState<'state> -> 'view
          assertion: 'payload -> CellState<'state>
          restriction: 'view -> CellState<'state>
          payload: 'state -> 'payload option
          inject: 'payload -> 'state }

/// Domain-shaped syntax over one live GeneralNet. Premise dictionaries only translate user-facing
/// names to opaque core handles and back for provenance display.
type Network<'state, 'payload, 'view when 'state : equality>(adapter: PayloadAdapter<'state, 'payload, 'view>) =
    let premiseIds = Dictionary<string, Premise>()
    let premiseNames = Dictionary<int, string>()

    let premiseId name =
        match premiseIds.TryGetValue name with
        | true, premise -> premise
        | _ ->
            let premise = General.freshPremise adapter.core
            premiseIds.[name] <- premise
            premiseNames.[premise.pid] <- name
            premise

    member _.Cell (name: string) = General.cell adapter.core name

    member _.Convert
        (left: Cell<'state>,
         right: Cell<'state>,
         forward: 'payload -> 'payload,
         backward: 'payload -> 'payload) =
        General.convert adapter.core left right adapter.payload adapter.inject forward backward

    member _.Combine
        (sources: Cell<'state> list,
         target: Cell<'state>,
         operation: 'payload list -> 'payload) =
        General.combine adapter.core sources target adapter.payload adapter.inject operation

    member _.Constrain (constraintBox: Constraint<'state>) =
        General.constrain adapter.core constraintBox

    member _.Given (cell: Cell<'state>, value: 'payload) =
        let premise = General.freshPremise adapter.core
        premiseNames.[premise.pid] <- sprintf "given(%s)" cell.name
        General.assertStateUnder adapter.core premise cell (adapter.assertion value)

    member _.Assume (premise: string, cell: Cell<'state>, value: 'payload) =
        General.assertStateUnder adapter.core (premiseId premise) cell (adapter.assertion value)

    member _.Restrict (cell: Cell<'state>, view: 'view) =
        let premise = General.freshPremise adapter.core
        premiseNames.[premise.pid] <- sprintf "restrict(%s)" cell.name
        General.assertStateUnder adapter.core premise cell (adapter.restriction view)

    member _.Restrict (premise: string, cell: Cell<'state>, view: 'view) =
        General.assertStateUnder adapter.core (premiseId premise) cell (adapter.restriction view)

    member _.Retract (premise: string) =
        match premiseIds.TryGetValue premise with
        | true, found -> General.retract adapter.core found
        | _ -> ()

    member _.Value (cell: Cell<'state>) =
        General.value adapter.core cell |> adapter.read

    member _.Support (cell: Cell<'state>) = General.support adapter.core cell

    member _.Solve () = General.solve adapter.core

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

module Domain =

    let scalar<'value when 'value : equality> () : Network<Scalar<'value>, 'value, Scalar<'value>> =
        let domain =
            ``Propagator-surface-vocab``.Domain.lattice
                Top scalarMeet (function Bot -> true | _ -> false)
        let core = General.createLattice domain
        Network
            { core = core
              read = function LatticeValue value -> value | _ -> invalidOp "scalar state mismatch"
              assertion = Val >> LatticeValue
              restriction = LatticeValue
              payload = function Val value -> Some value | _ -> None
              inject = Val }

    let interval () : Network<Interval, Interval, Interval> =
        let domain =
            ``Propagator-surface-vocab``.Domain.lattice
                Interval.entire Interval.meet (function Empty -> true | _ -> false)
        let core = General.createLattice domain
        Network
            { core = core
              read = function LatticeValue value -> value | _ -> invalidOp "interval state mismatch"
              assertion = LatticeValue
              restriction = LatticeValue
              payload = function Empty -> None | interval -> Some interval
              inject = id }

    let finite (values: 'value list) : Network<'value, 'value, Set<'value>> when 'value : comparison =
        let core = General.createFinite values
        Network
            { core = core
              read = function
                  | FiniteCandidates candidates -> Set.ofList candidates
                  | _ -> invalidOp "finite state mismatch"
              assertion = fun value -> FiniteCandidates [ value ]
              restriction = fun allowed ->
                  FiniteCandidates
                      [ for value in values do
                            if Set.contains value allowed then yield value ]
              payload = Some
              inject = id }

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
    let assume premise cell value (network: Network<'state, 'payload, 'view>) = network.Assume(premise, cell, value)
    let given cell value (network: Network<'state, 'payload, 'view>) = network.Given(cell, value)
    let restrict cell view (network: Network<'state, 'payload, 'view>) = network.Restrict(cell, view)
    let restrictNamed premise cell view (network: Network<'state, 'payload, 'view>) =
        network.Restrict(premise, cell, view)
    let retract premise (network: Network<'state, 'payload, 'view>) = network.Retract premise
    let read cell (network: Network<'state, 'payload, 'view>) = network.Value cell
    let solve (network: Network<'state, 'payload, 'view>) = network.Solve ()
