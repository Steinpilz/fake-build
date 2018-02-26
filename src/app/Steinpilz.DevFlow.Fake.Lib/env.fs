[<AutoOpen>]
module Env

open Fake
open System.Text.RegularExpressions

type EnvLine = { Key: string; RawValue: string; Value: string; LineNumber: int }

let getRawVal el = el.RawValue
let getVal el = el.Value
let setRawVal value el = { el with RawValue = value }

let envFileName = ".env"
let envPath dir = dir @@ envFileName

let tryGet k env =
    env |> Map.tryFind k

let get k env =
    env |> tryGet k |> Option.defaultWith (fun _ -> failwithf "can not find '%s' in env" k)

let set k v env =
    // Value is abandoned
    let envLine = env |> get k |> setRawVal v
    env |> Map.add k envLine

let processRawEnv env =
    let mutable resolvedEnv = Map.empty
    let mutable visiting = Set.empty
    let rec resolve k =
        resolvedEnv |> Map.tryFind k |> Option.defaultWith (fun _ ->
            if visiting |> Set.contains k then failwithf "there is a cycle in the env"
            visiting <- visiting |> Set.add k

            env |> tryGet k |> Option.map (fun x -> x.RawValue)
            |> Option.orElseWith (fun _ -> environVarOrNone k)
            |> Option.map (fun rawVal ->
                let matchEvaluator (m: Match) = resolve m.Groups.[1].Value
                let resVal = Regex("%(.*?)%").Replace(rawVal, matchEvaluator)
                resolvedEnv <- resolvedEnv |> Map.add k resVal
                resVal
            )
            |> Option.defaultValue (sprintf "%%%s%%" k)
        )
    env
    |> Map.map(fun k v -> { v with Value = k |> resolve })

let read dir =
    let path = envPath dir
    let fileLines = ReadFile path
    fileLines
        |> Seq.map (fun x -> x |> split '=')
        |> Seq.filter (fun x -> List.length x = 2)
        |> Seq.mapi (fun i x ->
            let key = x |> List.item 0
            let rawVal = x |> List.item 1
            (key, { Key = key; RawValue = rawVal; Value = null; LineNumber = i })
        )
        |> Map.ofSeq
        |> processRawEnv

let write dir env =
    let path = envPath dir
    let fileLines =
        env
        |> Map.toSeq
        |> Seq.sortBy (fun (_, v) -> v.LineNumber)
        |> Seq.map (fun (k, v) -> k + "=" + v.RawValue)
    WriteFile path fileLines

let setEnvVars env =
    env |> Map.iter (fun _ v ->
        System.Environment.SetEnvironmentVariable(v.Key, v.Value)
    )
