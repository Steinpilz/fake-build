module Pub

open System
open Fake
open Fake.Git
open Fake.SemVerHelper


type PubParams = {
    WorkingDir: string
    Args: string list
}
    
let defaultParams = {
    WorkingDir = "." |> FullName
    Args =
        Environment.GetCommandLineArgs()
        |> Seq.skipWhile (fun x -> x.ToLower() <> "pub")
        |> Seq.skip 1
        |> Seq.toList
}


let (|Prefix|_|) (p:string) (s:string) =
    match s.StartsWith(p) with
    | true -> Some(s.Substring(p.Length))
    | false -> None


let envFileName = ".env"
let envPath dir = dir @@ envFileName

let envRead dir =
    let path = envPath dir
    let fileLines = ReadFile path
    fileLines
        |> Seq.map (fun x -> x |> split '=')
        |> Seq.filter (fun x -> List.length x = 2)
        |> Seq.mapi (fun i x -> (x |> List.item 0, (x |> List.item 1, i)))
        |> Map.ofSeq

let envWrite dir env =
    let path = envPath dir
    let fileLines =
        env
        |> Map.toSeq
        |> Seq.sortBy (fun (k, (v, i)) -> i)
        |> Seq.map (fun (k, (v, i)) -> k + "=" + v)
    WriteFile path fileLines

let envTryGet k env =
    env |> Map.tryFind k |> Option.map fst

let envGet k env =
    env |> envTryGet k |> Option.get

let envSet k v env =
    let pos = env |> Map.find k |> snd
    env |> Map.add k (v, pos)


let normPreRelease ver =
    let num = ver.Number
    let resStr = num |> Option.map (fun x -> x.ToString "000")
    resStr |> Option.bind PreRelease.TryParse |> Option.get

let incPreRelease ver =
    let num = ver.Number |> Option.get |> ((+) 1)
    { ver with Number = Some(num) } |> normPreRelease

let zeroPreRelease = PreRelease.TryParse "001" |> Option.get


// Method not found: '!!0 Microsoft.FSharp.Core.OptionModule.DefaultValue
let optionDefaultValue value =
    function
    | Some x -> x
    | _ -> value

let optionDefaultWith value =
    function
    | Some x -> x
    | _ -> value()


let incPrev ver =
    { ver with PreRelease = ver.PreRelease |> Option.map incPreRelease |> optionDefaultValue zeroPreRelease |> Some }

let incPatch ver =
    { ver with PreRelease = None; Patch = ver.Patch + 1 }

let incMinor ver =
    { ver with PreRelease = None; Patch = 0; Minor = ver.Minor + 1; }

let incMajor ver =
    { ver with PreRelease = None; Patch = 0; Minor = 0; Major = ver.Major + 1; }

let resetPrev ver =
    { ver with PreRelease = None }

let verToTagStr ver =
    sprintf "v%d.%d.%d" ver.Major ver.Minor ver.Patch

let verToVp withMv ver =
    match withMv with
    | true -> sprintf "%%mv%%.%d.%d" ver.Minor ver.Patch
    | false -> sprintf "%d.%d.%d" ver.Major ver.Minor ver.Patch
    
let verToVs ver =
    ver.PreRelease |> Option.bind (fun x -> x.Number) |> Option.map(fun x -> sprintf "preview%03d" x) |> optionDefaultValue ""


// can be maded more secure (mv existence and vp=%mv%... check)

let vsToFakeFormat (s: string) =
    let p = "preview"
    if (s.ToLower().StartsWith(p) && not (s.Substring(p.Length).StartsWith("-")))
    then
        s.Insert(p.Length, "-")
    else
        s

let readVerFromEnv env =
    let segs = [
        env |> envTryGet "mv"
        Some(env |> envGet "vp" |> replace "%mv%." "")
        Some(env |> envGet "vs" |> vsToFakeFormat)
    ]
    let verStr = segs |> Seq.filter Option.isSome |> Seq.map Option.get |> String.concat "."
    SemVerHelper.parse verStr

let writeVerToEnv env ver =
    let hasMv = env |> Map.containsKey "mv"
    let trs = [
        (fun x -> if hasMv then x |> envSet "mv" (ver.Major.ToString()) else x)
        (fun x -> x |> envSet "vp" (ver |> verToVp hasMv))
        (fun x -> x |> envSet "vs" (verToVs ver))
    ]
    let tr = trs |> Seq.fold (>>) id
    env |> tr


let gitCommitAmend rep = 
    gitCommand rep "commit --amend --no-edit"

let gitCommit rep msg = 
    gitCommand rep (sprintf "commit -m \"%s\"" msg)

let gitGetLastTag rep =
    let res = getGitResult rep "git describe --abbrev=0 --tags"
    assert (res.Count < 2)
    Seq.tryHead res

let gitFetch rep = gitCommand rep "fetch --tags"

let gitPush rep = gitCommand rep "push --tags"

let gitIsStageEmpty rep = (getGitResult rep "diff --staged").Count = 0

let gitIsHeadTagged rep =
    let tags = getGitResult rep "tag --points-at HEAD"
    tags.Count > 0

let gitEnsureStageEmpty msg rep = 
    if not (gitIsStageEmpty rep) then failwith msg

let gitEnsureStageNonempty msg rep = 
    if gitIsStageEmpty rep then failwith msg

type GitSyncStatus = { Behind: bool; Ahead: bool; }

let gitGetSyncStatus rep =
    let status = getGitResult rep "status -sb"
    assert (status.Count > 0)
    let diff = (status.Item 0).Split([| '[' |], 2) |> Array.tryItem 1
    match diff with
    | Some x -> { Behind = x.Contains("behind"); Ahead = x.Contains("ahead"); }
    | _ -> { Behind = false; Ahead = false; }

let gitEnsureHeadWithoutTags msg rep =
    if gitIsHeadTagged rep then failwith msg


type ReleaseInc =
| Patch
| Minor
| Major

type VerInc = 
| ReleaseInc of ReleaseInc
| PreviewNew of ReleaseInc
| PreviewNext
| PreviewRelease

type Cmd = { VerInc: VerInc; Msg: string option; }

let parseArgs args =
    let parsePureIncVer = function
    | Prefix "patch" _ -> Patch
    | Prefix "min" _ ->  Minor
    | Prefix "maj" _ -> Major
    | x -> failwithf "can not handle '%s', supported: patch, minor, major" x

    let fArg =
        args
        |> List.tryItem 0
        |> optionDefaultWith (fun _ -> failwith "you have not passed a cmd, you can use patch, minor, major, prePatch, preMinor, preMajor, pre, release; you can use it like ./build pub pre or ./build pub pre \"a message here\"")
        |> toLower

    let verInc =
        match fArg with
        | Prefix "pre" _ ->
            match fArg.Substring(3) with
            | "" -> PreviewNext
            | fArgSuff -> PreviewNew (fArgSuff |> parsePureIncVer)
        | Prefix "rel" _ -> PreviewRelease
        | _ -> ReleaseInc (fArg |> parsePureIncVer)
    
    let msg = args |> List.tryItem 1

    { VerInc = verInc; Msg = msg; }


let ensureVerIsRelease msg ver = if Option.isNone ver.PreRelease then ver else failwith msg
let ensureVerIsPreRelease msg ver = if Option.isSome ver.PreRelease then ver else failwith msg

let makeReleaseInc relInc ver = 
    let ver = ver |> ensureVerIsRelease "you can not change version until the current version is preview, you can use 'pre' cmd to inc a preview version or 'release' cmd to release a preview"
    match relInc with
    | Patch -> incPatch ver
    | Minor -> incMinor ver
    | Major -> incMajor ver

let makeVerInc verInc ver =
    let previewEnsure = ensureVerIsPreRelease "you can not use pre-prelease cmds ('pre', 'release') if the current version is not preview, you need to use prePatch, preMinor or preMajor to create a preview"
    match verInc with
    | ReleaseInc x -> ver |> makeReleaseInc x
    | PreviewNew x ->  ver |> makeReleaseInc x |> incPrev
    | PreviewNext -> ver |> previewEnsure |> incPrev
    | PreviewRelease -> ver |> previewEnsure |> resetPrev


let setup setParams =
    let p = setParams defaultParams

    Target "Pub" <| (fun _ -> 
        let rep = p.WorkingDir
        let cmd = parseArgs p.Args
        let isRelease =
            match cmd.VerInc with
            | PreviewNew _ | PreviewNext -> false
            | ReleaseInc _ | PreviewRelease -> true

        log "init"
        let oldEnv = envRead rep
        let oldVer = readVerFromEnv oldEnv
        let newVer = oldVer |> makeVerInc cmd.VerInc
        let newEnv = newVer |> writeVerToEnv oldEnv
        
        log "validation"
        match cmd.VerInc with
        | PreviewNext -> ()
        | _ ->  gitFetch rep
        let status = gitGetSyncStatus rep
        if status.Behind then failwith "there are unmerged commits on the upstream branch, merge them first" else ()
        match cmd.Msg with
        | Some _ ->
            if gitIsHeadTagged rep then rep |> gitEnsureStageNonempty "there is a tag on the last commit (that's release) and there are no staged changes, you can not make a release without changes" else ()
        | None ->
            match status.Ahead with
            | true ->
                gitEnsureHeadWithoutTags "there is a tag on the last commit, add a commit message or create a commit manually" rep
                gitEnsureStageEmpty "there are some changes (stage is not empty), add a commit message or create a commit manually" rep // "because you have not added a message the build going to amend the last commit and, but it is not so" rep
            | false ->
                failwith "the last commit exists on the upstream, add a commit message or create a commit manually"
        
        if isRelease then
            log "tests"
            TargetHelper.run "Test"
        else ()

        log ".env updating, commiting"
        envWrite rep newEnv
        StageFile rep envFileName |> ignore
        match cmd.Msg with
        | Some x ->
            gitCommit rep x
        | None ->
            gitCommitAmend rep
        
        if isRelease then
            log "tagging, pushing, publishing"
            tag rep (verToTagStr newVer)
            //gitPush rep
            //TargetHelper.run "Publish"
        else ()

        ()
    )
