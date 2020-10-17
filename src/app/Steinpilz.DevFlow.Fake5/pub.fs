module Steinpilz.DevFlow.Fake.Pub

open System
open Fake.Core
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.Tools

type PubParams = {
    WorkingDir: string
    Args: string list
}
    
let defaultParams = {
    WorkingDir = "." |> Path.getFullName
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
    let fileLines = File.read path
    fileLines
        |> Seq.map (fun x -> x |> String.split '=')
        |> Seq.filter (fun x -> List.length x = 2)
        |> Seq.mapi (fun i x -> (x |> List.item 0, (x |> List.item 1, i)))
        |> Map.ofSeq

let envWrite dir env =
    let path = envPath dir
    let fileLines =
        env
        |> Map.toSeq
        |> Seq.sortBy (fun (_, (_, i)) -> i)
        |> Seq.map (fun (k, (v, _)) -> k + "=" + v)
    File.writeNew path fileLines

let envTryGet k env =
    env |> Map.tryFind k |> Option.map fst

let envGet k env =
    env |> envTryGet k |> Option.get

let envSet k v env =
    let pos = env |> Map.find k |> snd
    env |> Map.add k (v, pos)


let prereleaseNumber prerelease =
    prerelease.Values |> List.map(function Numeric num -> Some num | _ -> None) |> List.choose id |> List.tryHead

let normPreRelease (ver: PreRelease) =
    let num = prereleaseNumber ver
    let resStr = num |> Option.map (fun x -> x.ToString "000")
    resStr |> Option.bind PreRelease.TryParse |> Option.get

let incPreRelease (ver: PreRelease) =
    let num = ver |> prereleaseNumber |> Option.get |> ((+) (bigint 1)) |> Numeric
    { ver with Values = [num] } |> normPreRelease

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


let incPrev (ver: SemVerInfo) =
    { ver with PreRelease = ver.PreRelease |> Option.map incPreRelease |> optionDefaultValue zeroPreRelease |> Some }

let incPatch (ver: SemVerInfo) =
    { ver with PreRelease = None; Patch = ver.Patch + 1u }

let incMinor (ver: SemVerInfo) =
    { ver with PreRelease = None; Patch = 0u; Minor = ver.Minor + 1u; }

let incMajor (ver: SemVerInfo) =
    { ver with PreRelease = None; Patch = 0u; Minor = 0u; Major = ver.Major + 1u; }

let resetPrev (ver: SemVerInfo) =
    { ver with PreRelease = None }

let verToTagStr ver =
    sprintf "v%d.%d.%d" ver.Major ver.Minor ver.Patch

let verToVp withMv ver =
    match withMv with
    | true -> sprintf "%%mv%%.%d.%d" ver.Minor ver.Patch
    | false -> sprintf "%d.%d.%d" ver.Major ver.Minor ver.Patch

let verToVs ver =
    ver.PreRelease |> Option.bind prereleaseNumber |> Option.map(fun x -> sprintf "preview%03A" x) |> optionDefaultValue ""


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
        Some(env |> envGet "vp" |> String.replace "%mv%." "")
        Some(env |> envGet "vs" |> vsToFakeFormat)
    ]
    let verStr = segs |> Seq.filter Option.isSome |> Seq.map Option.get |> String.concat "."
    SemVer.parse verStr

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
    Git.CommandHelper.gitCommand rep "commit --amend --no-edit"

let gitCommit rep msg = 
    Git.CommandHelper.gitCommand rep (sprintf "commit -m \"%s\"" msg)

let gitGetLastTag rep =
    let res = Git.CommandHelper.getGitResult rep "git describe --abbrev=0 --tags"
    assert (List.length res < 2)
    Seq.tryHead res

let gitFetch rep = Git.CommandHelper.gitCommand rep "fetch --tags"

let gitPush rep = Git.CommandHelper.gitCommand rep "push --tags"

let gitIsStageEmpty rep = (Git.CommandHelper.getGitResult rep "diff --staged") |> List.length = 0

let gitIsHeadTagged rep =
    let tags = Git.CommandHelper.getGitResult rep "tag --points-at HEAD"
    List.length tags > 0

let gitEnsureStageEmpty msg rep = 
    if not (gitIsStageEmpty rep) then failwith msg

let gitEnsureStageNonempty msg rep = 
    if gitIsStageEmpty rep then failwith msg

type GitSyncStatus = { Behind: bool; Ahead: bool; }

let gitGetSyncStatus rep =
    let status = Git.CommandHelper.getGitResult rep "status -sb"
    assert (List.length status > 0)
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

type Pub = { VerInc: VerInc; Msg: string option; }

type Cmd =
| Pub of Pub
| Help

let parseArgs args =
    let parseReleaseInc = function
    | Prefix "pat" _ -> Patch
    | Prefix "min" _ ->  Minor
    | Prefix "maj" _ -> Major
    | x -> failwithf "can not handle '%s', you can use 'pub help' command (or just 'pub' without a command)" x

    let fArg =
        args
        |> List.tryItem 0
        |> Option.map String.toLower
    
    match fArg with
    | None -> Help
    | Some fArg ->
        match fArg with
        | "help" -> Help
        | _ ->
            let verInc =
                match fArg with
                | Prefix "pre" _ ->
                    match fArg.Substring(3) with
                    | "" -> PreviewNext
                    | fArgSuff -> PreviewNew (fArgSuff |> parseReleaseInc)
                | Prefix "rel" _ -> PreviewRelease
                | _ -> ReleaseInc (fArg |> parseReleaseInc)
    
            let msg = args |> List.tryItem 1

            Pub ({ VerInc = verInc; Msg = msg; })


let ensureVerIsRelease msg ver = if Option.isNone ver.PreRelease then ver else failwith msg
let ensureVerIsPreRelease msg ver = if Option.isSome ver.PreRelease then ver else failwith msg

let makeReleaseInc relInc ver = 
    let ver = ver |> ensureVerIsRelease "you can not change version until the current version is a preview, you can use 'pre' cmd to inc a preview version or 'release' cmd to release a preview"
    match relInc with
    | Patch -> incPatch ver
    | Minor -> incMinor ver
    | Major -> incMajor ver

let makeVerInc verInc ver =
    let previewEnsure = ensureVerIsPreRelease "you can not use pre-prelease cmds ('pre', 'release') if the current version is not a preview, you need to use prePatch, preMinor or preMajor to create a preview"
    match verInc with
    | ReleaseInc x -> ver |> makeReleaseInc x
    | PreviewNew x ->  ver |> makeReleaseInc x |> incPrev
    | PreviewNext -> ver |> previewEnsure |> incPrev
    | PreviewRelease -> ver |> previewEnsure |> resetPrev


let setup setParams =
    let p = setParams defaultParams

    Target.create "Pub" <| (fun _ -> 
        let rep = p.WorkingDir
        let cmd = parseArgs p.Args

        match cmd with
        | Help -> 
            Trace.log "# How to use Pub"
            Trace.log "Well, if you want to make a preview just type 'pub prePatch', 'pub preMinor', 'pub preMajor', it will fetch the upstream (i.e. the remote branch) for validation and will not run tests."
            Trace.log "All commands are case-insensitive, so a big letter just for readibility."
            Trace.log "Ok, now you have preview001, you want to make next preview, you can just type 'pub pre' for that, it does not fetch the upstream and does not run tests."
            Trace.log "If you think that a preview should be released, just type 'pub release', it will fetch the upstream, run tests, set a version tag and make a push (with tags) to the upstream."
            Trace.log "It's important that all commands run 'Publish' target to deploy each version to NuGet."
            Trace.log "Also you can shorten 'pub preXyz' with just 'pub xyz' (e.g. 'pub patch') if you do not need a preview of course."
            Trace.log "You can use a prefix only, patch=pat, minor=min, major=maj, release=rel."
            Trace.log ""
            Trace.log "# Important info for understanding the workflow"
            Trace.log "Brief: Pub realization assumes that you'll make some changes, stage them, then call Pub with a commit message; then you can use Pub without a message to just change a version of the last commit (and deploy it like all cmds do of course)."
            Trace.log "You can pass a commit message, so 'pub patch \"some msg\"' will create a commit (it will not call smth like 'git add .', you should do it yourself)."
            Trace.log "If a commit message is passed then Pub will always create a new commit and it will always amend the last commit if a commit message is not passed."
            Trace.log "Because of that a stage must be nonempty if a commit message is passed and it must be empty if a commit message is not passed."
        | Pub pub ->
            let verInc = pub.VerInc
            let msg = pub.Msg

            let isRelease =
                match verInc with
                | PreviewNew _ | PreviewNext -> false
                | ReleaseInc _ | PreviewRelease -> true

            Trace.log "init"
            let oldEnv = envRead rep
            let oldVer = oldEnv |> readVerFromEnv
            let newVer = oldVer |> makeVerInc verInc
            let newEnv = newVer |> writeVerToEnv oldEnv

            Trace.log "validation"
            match verInc with
            | PreviewNext -> ()
            | _ ->  rep |> gitFetch
            let status = rep |> gitGetSyncStatus
            if status.Behind then failwith "there are unmerged commits on the upstream branch, merge them first"
            match msg with
            | Some _ ->
                rep |> gitEnsureStageNonempty "there are no staged changes, you can not make a commit without changes"
            | None ->
                match status.Ahead with
                | true ->
                    rep |> gitEnsureHeadWithoutTags "there is a tag on the last commit, add a commit message or create a commit manually"
                    rep |> gitEnsureStageEmpty "there are some changes (the stage is not empty), add a commit message or create a commit manually"
                | false ->
                    failwith "the last commit exists on the upstream, add a commit message or create a commit manually"

            if isRelease then
                Trace.log "tests"
                Target.runOrDefault "Test"

            Trace.log ".env updating, commiting"
            newEnv |> envWrite rep
            envFileName |> Git.Staging.stageFile rep |> ignore
            match msg with
            | Some msg ->
                gitCommit rep msg
            | None ->
                gitCommitAmend rep

            if isRelease then
                Trace.log "tagging, pushing"
                verToTagStr newVer |> Git.Branches.tag rep
                rep |> gitPush

            Trace.log "publishing"
            Target.runOrDefault "Publish"

            ()
    )
