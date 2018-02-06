module Pub

open System
open Fake
open Fake.Git
open Fake.SemVerHelper
open Env


let (|Prefix|_|) (p:string) (s:string) =
    match s.StartsWith(p) with
    | true -> Some(s.Substring(p.Length))
    | false -> None

module Seq =
    let skipSafe count seq = seq |> Seq.indexed |> Seq.filter(fst >> (<=) count) |> Seq.map snd


type PubParams = {
    WorkingDir: string
    Args: string list
}

let defaultParams = {
    WorkingDir = "." |> FullName
    Args =
        Environment.GetCommandLineArgs()
        |> Seq.skipWhile (fun x -> x.ToLower() <> "pub")
        |> Seq.skipSafe 1
        |> Seq.toList
}


let normPreRelease ver =
    let num = ver.Number
    let resStr = num |> Option.map (fun x -> x.ToString "000")
    resStr |> Option.bind PreRelease.TryParse |> Option.get

let incPreRelease ver =
    let num = ver.Number |> Option.get |> ((+) 1)
    { ver with Number = Some(num) } |> normPreRelease

let zeroPreRelease = PreRelease.TryParse "001" |> Option.get


let incPrev ver =
    { ver with PreRelease = ver.PreRelease |> Option.map incPreRelease |> Option.defaultValue zeroPreRelease |> Some }

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
    ver.PreRelease |> Option.bind (fun x -> x.Number) |> Option.map(fun x -> sprintf "preview%03d" x) |> Option.defaultValue ""


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
        env |> Env.tryGet "mv" |> Option.map getRawVal
        Some(env |> Env.get "vp" |> getRawVal |> replace "%mv%." "")
        Some(env |> Env.get "vs" |> getRawVal |> vsToFakeFormat)
    ]
    let verStr = segs |> Seq.filter Option.isSome |> Seq.map Option.get |> String.concat "."
    SemVerHelper.parse verStr

let writeVerToEnv env ver =
    let hasMv = env |> Map.containsKey "mv"
    let trs = [
        (fun x -> if hasMv then x |> Env.set "mv" (ver.Major.ToString()) else x)
        (fun x -> x |> Env.set "vp" (ver |> verToVp hasMv))
        (fun x -> x |> Env.set "vs" (verToVs ver))
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
        |> Option.map toLower

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

    Target "Pub" <| (fun _ ->
        let rep = p.WorkingDir
        let cmd = parseArgs p.Args

        match cmd with
        | Help ->
            log "# How to use Pub"
            log "Well, if you want to make a preview just type 'pub prePatch', 'pub preMinor', 'pub preMajor', it will fetch the upstream (i.e. the remote branch) for validation and will not run tests."
            log "All commands are case-insensitive, so a big letter just for readibility."
            log "Ok, now you have preview001, you want to make next preview, you can just type 'pub pre' for that, it does not fetch the upstream and does not run tests."
            log "If you think that a preview should be released, just type 'pub release', it will fetch the upstream, run tests, set a version tag and make a push (with tags) to the upstream."
            log "It's important that all commands run 'Publish' target to deploy each version to NuGet."
            log "Also you can shorten 'pub preXyz' with just 'pub xyz' (e.g. 'pub patch') if you do not need a preview of course."
            log "You can use a prefix only, patch=pat, minor=min, major=maj, release=rel."
            log ""
            log "# Important info for understanding the workflow"
            log "Brief: Pub implementation assumes that you'll make some changes, stage them, then call Pub with a commit message; then you can use Pub without a message to just change a version of the last commit (and deploy it like all cmds do of course)."
            log "You can pass a commit message, so 'pub patch \"some msg\"' will create a commit (it will not call smth like 'git add .', you should do it yourself)."
            log "If a commit message is passed then Pub will always create a new commit and it will always amend the last commit if a commit message is not passed."
            log "Because of that a stage must be nonempty if a commit message is passed and it must be empty if a commit message is not passed."
        | Pub pub ->
            let verInc = pub.VerInc
            let msg = pub.Msg

            let isRelease =
                match verInc with
                | PreviewNew _ | PreviewNext -> false
                | ReleaseInc _ | PreviewRelease -> true

            log "init"
            let oldEnv = Env.read rep
            let oldVer = oldEnv |> readVerFromEnv
            let newVer = oldVer |> makeVerInc verInc
            let newEnv = newVer |> writeVerToEnv oldEnv

            log "validation"
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
                log "tests"
                TargetHelper.run "Test"

            log ".env updating, commiting"
            newEnv |> Env.write rep
            Env.envFileName |> StageFile rep |> ignore
            match msg with
            | Some msg ->
                gitCommit rep msg
            | None ->
                gitCommitAmend rep

            if isRelease then
                log "tagging, pushing"
                verToTagStr newVer |> tag rep
                rep |> gitPush

            log "publishing"
            TargetHelper.run "Publish"

            ()
    )
