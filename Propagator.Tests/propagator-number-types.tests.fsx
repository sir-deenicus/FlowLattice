#r "nuget: MathNet.Numerics.FSharp, 4.15.0"

open MathNet.Numerics
open Propagator.Core
open Propagator.Network

let private require label condition =
    if not condition then failwithf "number-type regression failed: %s" label

let private cToF x = x * 9.0 / 5.0 + 32.0
let private fToC x = (x - 32.0) * 5.0 / 9.0

let private scalarFloatFromC value =
    let net = Domain.scalar<float> ()
    let celsius = net.Cell "float-C"
    let fahrenheit = net.Cell "float-F"
    net.Convert(celsius, fahrenheit, cToF, fToC)
    net.Given(celsius, value)
    net.Value celsius, net.Value fahrenheit

let private scalarDecimalFromC value =
    let net = Domain.scalar<decimal> ()
    let celsius = net.Cell "decimal-C"
    let fahrenheit = net.Cell "decimal-F"
    net.Convert(
        celsius,
        fahrenheit,
        (fun x -> x * 9.0m / 5.0m + 32.0m),
        (fun x -> (x - 32.0m) * 5.0m / 9.0m))
    net.Given(celsius, value)
    net.Value celsius, net.Value fahrenheit

let private scalarDecimalFromF value =
    let net = Domain.scalar<decimal> ()
    let celsius = net.Cell "decimal-C"
    let fahrenheit = net.Cell "decimal-F"
    net.Convert(
        celsius,
        fahrenheit,
        (fun x -> x * 9.0m / 5.0m + 32.0m),
        (fun x -> (x - 32.0m) * 5.0m / 9.0m))
    net.Given(fahrenheit, value)
    net.Value celsius, net.Value fahrenheit

let private scalarRationalFromC value =
    let integer = BigRational.FromInt
    let nineFifths = integer 9 / integer 5
    let fiveNinths = integer 5 / integer 9
    let thirtyTwo = integer 32
    let net = Domain.scalar<BigRational> ()
    let celsius = net.Cell "rational-C"
    let fahrenheit = net.Cell "rational-F"
    net.Convert(
        celsius,
        fahrenheit,
        (fun x -> x * nineFifths + thirtyTwo),
        (fun x -> (x - thirtyTwo) * fiveNinths))
    net.Given(celsius, value)
    net.Value celsius, net.Value fahrenheit

let private scalarRationalFromF value =
    let integer = BigRational.FromInt
    let nineFifths = integer 9 / integer 5
    let fiveNinths = integer 5 / integer 9
    let thirtyTwo = integer 32
    let net = Domain.scalar<BigRational> ()
    let celsius = net.Cell "rational-C"
    let fahrenheit = net.Cell "rational-F"
    net.Convert(
        celsius,
        fahrenheit,
        (fun x -> x * nineFifths + thirtyTwo),
        (fun x -> (x - thirtyTwo) * fiveNinths))
    net.Given(fahrenheit, value)
    net.Value celsius, net.Value fahrenheit

let private intervalFromC value =
    let net = Domain.interval ()
    let celsius = net.Cell "interval-C"
    let fahrenheit = net.Cell "interval-F"
    net.Convert(
        celsius,
        fahrenheit,
        (fun x -> x * 9.0 / 5.0 + 32.0),
        (fun x -> (x - 32.0) * 5.0 / 9.0))
    net.Given(celsius, Interval.pt value)
    net.Value celsius, net.Value fahrenheit

let private intervalFromF value =
    let net = Domain.interval ()
    let celsius = net.Cell "interval-C"
    let fahrenheit = net.Cell "interval-F"
    net.Convert(
        celsius,
        fahrenheit,
        (fun x -> x * 9.0 / 5.0 + 32.0),
        (fun x -> (x - 32.0) * 5.0 / 9.0))
    net.Given(fahrenheit, Interval.pt value)
    net.Value celsius, net.Value fahrenheit

let private contains expected = function
    | Iv(low, high) -> low <= expected && expected <= high
    | Empty -> false

let private verifyRepresentativeCases () =
    let floatC, floatF = scalarFloatFromC 1.0
    require "float exposes exact-equality round-trip failure" (floatC = Bot && floatF = Val 33.8)

    let decimalC, decimalF = scalarDecimalFromC 1.0m
    require "decimal Celsius route remains exact" (decimalC = Val 1.0m && decimalF = Val 33.8m)
    let _, decimalF47 = scalarDecimalFromF 47.0m
    require "decimal Fahrenheit route exposes division residue" (decimalF47 = Bot)

    let integer = BigRational.FromInt
    let rationalC, rationalF = scalarRationalFromC (integer 1)
    require
        "rational Celsius route is exact"
        (rationalC = Val (integer 1) && rationalF = Val (integer 169 / integer 5))
    let rationalC47, rationalF47 = scalarRationalFromF (integer 47)
    require
        "rational Fahrenheit route is exact"
        (rationalC47 = Val (integer 25 / integer 3) && rationalF47 = Val (integer 47))

    let intervalC, intervalF = intervalFromC 1.0
    require "interval Celsius route encloses both values" (contains 1.0 intervalC && contains 33.8 intervalF)
    let intervalC47, intervalF47 = intervalFromF 47.0
    require "interval Fahrenheit route encloses both values" (contains (25.0 / 3.0) intervalC47 && contains 47.0 intervalF47)

    let conflict = Domain.interval ()
    let conflictC = conflict.Cell "conflict-C"
    let conflictF = conflict.Cell "conflict-F"
    conflict.Convert(
        conflictC,
        conflictF,
        (fun x -> x * 9.0 / 5.0 + 32.0),
        (fun x -> (x - 32.0) * 5.0 / 9.0))
    conflict.Assume("celsius", conflictC, Interval.pt 1.0)
    conflict.Assume("fahrenheit", conflictF, Interval.pt 100.0)
    require "interval lattice preserves genuine contradiction" (conflict.Value conflictF = Empty)

let private verifyHistoricalFailureCounts () =
    let floatCFailures =
        [ 0 .. 2000 ]
        |> List.sumBy (fun tick ->
            let value = float tick / 10.0
            let celsius, _ = scalarFloatFromC value
            if celsius = Bot then 1 else 0)

    let decimalCFailures =
        [ 0 .. 2000 ]
        |> List.sumBy (fun tick ->
            let value = decimal tick / 10m
            let celsius, _ = scalarDecimalFromC value
            if celsius = Bot then 1 else 0)

    let decimalFFailures =
        [ 0 .. 1800 ]
        |> List.sumBy (fun tick ->
            let value = decimal tick / 10m
            let _, fahrenheit = scalarDecimalFromF value
            if fahrenheit = Bot then 1 else 0)

    let rawDecimalFFailures =
        [ 0 .. 1800 ]
        |> List.sumBy (fun tick ->
            let value = decimal tick / 10m
            let roundTrip = (value - 32.0m) * 5.0m / 9.0m * 9.0m / 5.0m + 32.0m
            if roundTrip = value then 0 else 1)

    printfn "== numeric representation regressions =="
    printfn "  float C round-trip bottoms:   %d/2001" floatCFailures
    printfn "  decimal C round-trip bottoms: %d/2001" decimalCFailures
    printfn "  decimal F round-trip bottoms: %d/1801" decimalFFailures
    printfn "  direct decimal F mismatches:  %d/1801" rawDecimalFFailures
    require "float Celsius failure count remains 901/2001" (floatCFailures = 901)
    require "decimal Celsius failure count remains 0/2001" (decimalCFailures = 0)
    require
        "decimal scalar bottoms match direct arithmetic mismatches"
        (decimalFFailures = rawDecimalFFailures)
    require "decimal Fahrenheit division remains non-closed" (decimalFFailures > 0)

let private verifyFixedPointBoundary () =
    let tenths = FixedPoint(1.0m)
    let hundredths = FixedPoint(12.50m)
    require "fixed-point constructor infers tenths" (tenths.Quantum = 0.1m)
    require "fixed-point constructor preserves written hundredths" (hundredths.Quantum = 0.01m)
    require "decimal operators retain quantum" ((hundredths * 2m + 1m).Quantum = 0.01m)

    let mismatchRejected =
        try
            hundredths + FixedPoint(1.2m) |> ignore
            false
        with :? System.ArgumentException ->
            true
    require "fixed-point quantum inflation is rejected" mismatchRejected
    require
        "WithQuantum makes reconciliation explicit"
        ((hundredths.WithQuantum(0.1m) + FixedPoint(1.2m)).Quantum = 0.1m)

    let network = Domain.fixedPoint 0.1m
    let celsius = network.Cell "fixed-C"
    let fahrenheit = network.Cell "fixed-F"
    network.Convert(
        celsius,
        fahrenheit,
        (fun x -> x * 9m / 5m + 32m),
        (fun x -> (x - 32m) * 5m / 9m))
    network.Given(fahrenheit, FixedPoint(47.0m))
    require "fixed-point facade avoids a spurious Fahrenheit bottom" (not (network.Value fahrenheit).IsBottom)
    require "fixed-point facade preserves asserted grid point" ((network.Value fahrenheit).TryPoint = Some 47.0m)

verifyRepresentativeCases ()
verifyHistoricalFailureCounts ()
verifyFixedPointBoundary ()
printfn "  rational, interval, and fixed-point routes: PASS"
printfn ""
