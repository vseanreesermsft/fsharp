
module Tests.OptionBuilder

open System
open System.Runtime.CompilerServices
open FSharp.Core.CompilerServices
open FSharp.Core.CompilerServices.StateMachineHelpers

[<Struct; NoEquality; NoComparison>]
type OptionStateMachine<'T> =
    [<DefaultValue(false)>]
    val mutable Result : 'T voption

    member inline sm.ToOption() = match sm.Result with ValueNone -> None | ValueSome x -> Some x

    member inline sm.ToValueOption() = sm.Result 

    static member inline Run(sm: byref<'K> when 'K :> IAsyncStateMachine) = sm.MoveNext()

    interface IAsyncStateMachine with 
        member sm.MoveNext() = failwith "no dynamic impl"
        member sm.SetStateMachine(state: IAsyncStateMachine) = failwith "no dynamic impl"

type OptionCode<'T> = delegate of byref<OptionStateMachine<'T>> -> unit

type OptionBuilderBase() =

    member inline _.Delay([<ResumableCode>] __expand_f : unit -> OptionCode<'T>) : [<ResumableCode>] OptionCode<'T> =
        OptionCode (fun sm -> (__expand_f()).Invoke &sm)

    member inline _.Combine([<ResumableCode>] __expand_task1: OptionCode<unit>, [<ResumableCode>] __expand_task2: OptionCode<'T>) : [<ResumableCode>] OptionCode<'T> =
        OptionCode<_>(fun sm -> 
            let mutable sm2 = OptionStateMachine<unit>()
            __expand_task1.Invoke &sm2
            __expand_task2.Invoke &sm)

    member inline _.Bind(res1: 'T1 option, [<ResumableCode>] __expand_task2: ('T1 -> OptionCode<'T>)) : [<ResumableCode>] OptionCode<'T> =
        OptionCode<_>(fun sm -> 
            match res1 with 
            | None -> ()
            | Some v -> (__expand_task2 v).Invoke &sm)

    member inline _.Bind(res1: 'T1 voption, [<ResumableCode>] __expand_task2: ('T1 -> OptionCode<'T>)) : [<ResumableCode>] OptionCode<'T> =
        OptionCode<_>(fun sm -> 
            match res1 with 
            | ValueNone -> ()
            | ValueSome v -> (__expand_task2 v).Invoke &sm)
            
    member inline _.While([<ResumableCode>] __expand_condition : unit -> bool, [<ResumableCode>] __expand_body : OptionCode<unit>) : [<ResumableCode>] OptionCode<unit> =
        OptionCode<_>(fun sm -> 
            while __expand_condition() do
                __expand_body.Invoke &sm)

    member inline _.TryWith([<ResumableCode>] __expand_body : OptionCode<'T>, [<ResumableCode>] __expand_catch : exn -> OptionCode<'T>) : [<ResumableCode>] OptionCode<'T> =
        OptionCode<_>(fun sm -> 
            try
                __expand_body.Invoke &sm
            with exn -> 
                (__expand_catch exn).Invoke &sm)

    member inline _.TryFinally([<ResumableCode>] __expand_body: OptionCode<'T>, compensation : unit -> unit) : [<ResumableCode>] OptionCode<'T> =
        OptionCode<_>(fun sm -> 
            try
                __expand_body.Invoke &sm
            with _ ->
                compensation()
                reraise()

            compensation())

    member inline this.Using(disp : #IDisposable, [<ResumableCode>] __expand_body : #IDisposable -> OptionCode<'T>) : [<ResumableCode>] OptionCode<'T> = 
        // A using statement is just a try/finally with the finally block disposing if non-null.
        this.TryFinally(
            (fun sm -> (__expand_body disp).Invoke &sm),
            (fun () -> if not (isNull (box disp)) then disp.Dispose()))

    member inline this.For(sequence : seq<'TElement>, [<ResumableCode>] __expand_body : 'TElement -> OptionCode<unit>) : [<ResumableCode>] OptionCode<unit> =
        this.Using (sequence.GetEnumerator(), 
            (fun e -> this.While((fun () -> e.MoveNext()), (fun sm -> (__expand_body e.Current).Invoke &sm))))

    member inline _.Return (value: 'T) : [<ResumableCode>] OptionCode<'T> =
        OptionCode<_>(fun sm ->
            sm.Result <- ValueSome value)

    member inline this.ReturnFrom (source: option<'T>) : [<ResumableCode>] OptionCode<'T> =
        OptionCode<_>(fun sm ->
            sm.Result <- match source with Some x -> ValueOption.Some x | None -> ValueOption.None)

type OptionBuilder() =
    inherit OptionBuilderBase()
    

    member inline _.Run([<ResumableCode>] __expand_code : OptionCode<'T>) : 'T option = 
        if __useResumableStateMachines then
            __structStateMachine<OptionStateMachine<'T>, 'T option>
                (MoveNextMethod<_>(fun sm -> 
                       __expand_code.Invoke(&sm)))

                // SetStateMachine
                (SetMachineStateMethod<_>(fun sm state -> ()))

                // Start
                (AfterMethod<_,_>(fun sm -> 
                    OptionStateMachine<_>.Run(&sm)
                    sm.ToOption()))
        else
            let mutable sm = OptionStateMachine<'T>()
            __expand_code.Invoke(&sm)
            sm.ToOption()

type ValueOptionBuilder() =
    inherit OptionBuilderBase()
    

    member inline _.Run([<ResumableCode>] __expand_code : OptionCode<'T>) : 'T voption = 
        if __useResumableStateMachines then
            __structStateMachine<OptionStateMachine<'T>, 'T voption>
                (MoveNextMethod<OptionStateMachine<'T>>(fun sm -> 
                       __expand_code.Invoke(&sm)
                       ))

                // SetStateMachine
                (SetMachineStateMethod<_>(fun sm state -> 
                    ()))

                // Start
                (AfterMethod<_,_>(fun sm -> 
                    OptionStateMachine<_>.Run(&sm)
                    sm.ToValueOption()))
        else
            let mutable sm = OptionStateMachine<'T>()
            __expand_code.Invoke(&sm)
            sm.ToValueOption()

let option = OptionBuilder()
let voption = ValueOptionBuilder()

module Examples =

    let t1 () = 
        option {
           printfn "in t1"
           return "a"
        }

    let t2 () = 
        option {
           printfn "in t2"
           let! x = t1 ()
           return "f"
        }
    printfn "t1() = %A" (t1())
    printfn "t2() = %A" (t2())

    type SlowOptionBuilder() =
        member inline _.Zero() = None

        member inline _.Return(x: 'T) = Some x

        member inline _.ReturnFrom(m: 'T option) = m

        member inline _.Bind(m: 'T option, f) = Option.bind f m

        member inline _.Delay(f: unit -> _) = f

        member inline _.Run(f) = f()

        member this.TryWith(delayedExpr, handler) =
            try this.Run(delayedExpr)
            with exn -> handler exn

        member this.TryFinally(delayedExpr, compensation) =
            try this.Run(delayedExpr)
            finally compensation()

        member this.Using(resource:#IDisposable, body) =
            this.TryFinally(this.Delay(fun ()->body resource), fun () -> match box resource with null -> () | _ -> resource.Dispose())

    let optionSlow = SlowOptionBuilder()

    type SlowValueOptionBuilder() =
        member inline _.Zero() = ValueNone

        member inline _.Return(x: 'T) = ValueSome x

        member inline _.ReturnFrom(m: 'T voption) = m

        member inline _.Bind(m: 'T voption, f) = ValueOption.bind f m

        member inline _.Delay(f: unit -> _) = f

        member inline _.Run(f) = f()

        member inline this.TryWith(delayedExpr, handler) =
            try this.Run(delayedExpr)
            with exn -> handler exn

        member inline this.TryFinally(delayedExpr, compensation) =
            try this.Run(delayedExpr)
            finally compensation()

        member inline this.Using(resource:#IDisposable, body) =
            this.TryFinally(this.Delay(fun ()->body resource), fun () -> match box resource with null -> () | _ -> resource.Dispose())

    let voptionSlow = SlowValueOptionBuilder()

    let multiStepStateMachineBuilder () = 
        let mutable res = 0
        for i in 1 .. 1000000 do
            let v = 
                option {
                   try 
                      let! x1 = (if i % 5 <> 2 then Some i else None)
                      let! x2 = (if i % 3 <> 1 then Some i else None)
                      let! x3 = (if i % 3 <> 1 then Some i else None)
                      let! x4 = (if i % 3 <> 1 then Some i else None)
                      res <- res + 1 
                      return x1 + x2 + x3 + x4
                   with e -> 
                      return failwith "unexpected"
                } 
            v |> ignore
        res


    let multiStepStateMachineBuilderV () = 
        let mutable res = 0
        for i in 1 .. 1000000 do
            let v = 
                voption {
                   try 
                      let! x1 = (if i % 5 <> 2 then ValueSome i else ValueNone)
                      let! x2 = (if i % 3 <> 1 then ValueSome i else ValueNone)
                      let! x3 = (if i % 3 <> 1 then ValueSome i else ValueNone)
                      let! x4 = (if i % 3 <> 1 then ValueSome i else ValueNone)
                      res <- res + 1 
                      return x1 + x2 + x3 + x4
                   with e -> 
                      return failwith "unexpected"
                } 
            v |> ignore
        res

    let multiStepOldBuilder () = 
        let mutable res = 0
        for i in 1 .. 1000000 do
            let v = 
                optionSlow {
                   try 
                      let! x1 = (if i % 5 <> 2 then Some i else None)
                      let! x2 = (if i % 3 <> 1 then Some i else None)
                      let! x3 = (if i % 3 <> 1 then Some i else None)
                      let! x4 = (if i % 3 <> 1 then Some i else None)
                      res <- res + 1 
                      return x1 + x2 + x3 + x4
                   with e -> 
                      return failwith "unexpected"
                } 
            v |> ignore
        res

    let multiStepOldBuilderV () = 
        let mutable res = 0
        for i in 1 .. 1000000 do
            let v = 
                voptionSlow {
                   try 
                      let! x1 = (if i % 5 <> 2 then ValueSome i else ValueNone)
                      let! x2 = (if i % 3 <> 1 then ValueSome i else ValueNone)
                      let! x3 = (if i % 3 <> 1 then ValueSome i else ValueNone)
                      let! x4 = (if i % 3 <> 1 then ValueSome i else ValueNone)
                      res <- res + 1 
                      return x1 + x2 + x3 + x4
                   with e -> 
                      return failwith "unexpected"
                } 
            v |> ignore
        res

    let multiStepNoBuilder () = 
        let mutable res = 0
        for i in 1 .. 1000000 do
            let v = 
                try 
                    match (if i % 5 <> 2 then Some i else None) with
                    | None -> None
                    | Some x1 -> 
                    match (if i % 3 <> 1 then Some i else None) with
                    | None -> None
                    | Some x2 -> 
                    match (if i % 3 <> 1 then Some i else None) with
                    | None -> None
                    | Some x3 -> 
                    match (if i % 3 <> 1 then Some i else None) with
                    | None -> None
                    | Some x4 -> 
                    res <- res + 1 
                    Some (x1 + x2 + x3 + x4)
                with e -> 
                    failwith "unexpected"
            v |> ignore
        res

    let multiStepNoBuilderV () = 
        let mutable res = 0
        for i in 1 .. 1000000 do
            let v = 
                try 
                    match (if i % 5 <> 2 then ValueSome i else ValueNone) with
                    | ValueNone -> ValueNone
                    | ValueSome x1 -> 
                    match (if i % 3 <> 1 then ValueSome i else ValueNone) with
                    | ValueNone -> ValueNone
                    | ValueSome x2 -> 
                    match (if i % 3 <> 1 then ValueSome i else ValueNone) with
                    | ValueNone -> ValueNone
                    | ValueSome x3 -> 
                    match (if i % 3 <> 1 then ValueSome i else ValueNone) with
                    | ValueNone -> ValueNone
                    | ValueSome x4 -> 
                    res <- res + 1 
                    ValueSome (x1 + x2 + x3 + x4)
                with e -> 
                    failwith "unexpected"
            v |> ignore
        res

    // let perf s f = 
    //     let t = System.Diagnostics.Stopwatch()
    //     t.Start()
    //     for i in 1 .. 100 do 
    //         f() |> ignore
    //     t.Stop()
    //     printfn "PERF: %s : %d" s t.ElapsedMilliseconds

    // printfn "check %d = %d = %d"(multiStepStateMachineBuilder()) (multiStepNoBuilder()) (multiStepOldBuilder())

    // perf "perf (state mechine option)" multiStepStateMachineBuilder 
    // perf "perf (no builder option)" multiStepNoBuilder 
    // perf "perf (slow builder option)" multiStepOldBuilder 

    // printfn "check %d = %d = %d" (multiStepStateMachineBuilderV()) (multiStepNoBuilder()) (multiStepOldBuilder())
    // perf "perf (state mechine voption)" multiStepStateMachineBuilderV
    // perf "perf (no builder voption)" multiStepNoBuilderV
    // perf "perf (slow builder voption)" multiStepOldBuilderV
