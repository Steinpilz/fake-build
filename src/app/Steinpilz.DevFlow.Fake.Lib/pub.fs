module Pub

open System
open Fake
open Fake.Git
open Fake.SemVerHelper
open Env
open FParsec
open BuildParams
open BuildUtils

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

let defaultPubParams = {
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

let zeroPreRelease = PreRelease.TryParse "000" |> Option.get


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

let verToVp withMvVar ver =
    match withMvVar with
    | true -> sprintf "%%mv%%.%d.%d" ver.Minor ver.Patch
    | false -> sprintf "%d.%d.%d" ver.Major ver.Minor ver.Patch

let verToVsOpt ver =
    ver.PreRelease |> Option.bind (fun x -> x.Number) |> Option.map(fun x -> sprintf "preview%03d" x)

let verToVs =
    verToVsOpt >> Option.defaultValue ""


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


let gitAddAll rep =
    gitCommand rep "add -A"

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

type GitSyncStatus = { HasUpstream: bool; Behind: bool; Ahead: bool; }

let gitGetSyncStatus rep =
    let status = getGitResult rep "status -sb"
    assert (status.Count > 0)
    let fl = status.Item 0
    let hasUpstream = fl.Contains("...")
    let diff = fl.Split([| '[' |], 2) |> Array.tryItem 1
    match diff with
    | Some x -> { HasUpstream = hasUpstream; Behind = x.Contains("behind"); Ahead = x.Contains("ahead"); }
    | _ -> { HasUpstream = hasUpstream; Behind = false; Ahead = false; }

let gitEnsureHeadWithoutTags msg rep =
    if gitIsHeadTagged rep then failwith msg


type ReleaseInc =
| Patch
| Minor
| Major

type Pub =
| Release of ReleaseInc * string option
| PreviewNew of ReleaseInc
| PreviewNext of string option
| PreviewRelease of string option

type Cmd =
| Pub of Pub
| Help

let argsParser =
    let strUntilSep = many1CharsTill anyChar (lookAhead (spaces1 <|> eof))
    let releaseInc = (
        (skipStringCI "patch" <|> skipStringCI "pat" >>% Patch) <|>
        (skipStringCI "minor" <|> skipStringCI "min" >>% Minor) <|>
        (skipStringCI "major" <|> skipStringCI "maj" >>% Major)
    )
    let commitMessage =
        skipStringCI "-m" <|> skipStringCI "--message" >>. spaces1
        >>. many1CharsTill anyChar eof
        |> opt
    let previewRelease = skipStringCI "release" <|> skipStringCI "rel" >>. spaces >>. commitMessage |>> PreviewRelease
    let preview =
        skipStringCI "preview" <|> skipStringCI "pre" >>. spaces >>.
        ((releaseInc |>> PreviewNew) <|> previewRelease <|> (commitMessage |>> PreviewNext))
    let release = releaseInc .>> spaces .>>. commitMessage |>> Release
    let help = eof <|> skipStringCI "help" >>% Help
    help <|> (choice [release; preview] |>> Pub) .>> spaces .>> eof

let parseArgs args =
    match args |> String.concat " " |> run argsParser with
    | Failure (errorAsString, _, _) -> failwith errorAsString
    | Success (x, _, _) -> x


let ensureVerIsRelease msg ver = if Option.isNone ver.PreRelease then ver else failwith msg
let ensureVerIsPreRelease msg ver = if Option.isSome ver.PreRelease then ver else failwith msg

let makeReleaseInc relInc ver =
    let ver = ver |> ensureVerIsRelease "you can not change version of the current preview; type pub release to make a release"
    match relInc with
    | Patch -> incPatch ver
    | Minor -> incMinor ver
    | Major -> incMajor ver

let makeVerInc verInc ver =
    let previewEnsure = ensureVerIsPreRelease "you current version is not a preview; type pub preview (patch|minor|major) to make a preview"
    match verInc with
    | Release (x, _) -> ver |> makeReleaseInc x
    | PreviewNew x ->  ver |> makeReleaseInc x |> incPrev
    | PreviewNext _ -> ver |> previewEnsure |> incPrev
    | PreviewRelease _ -> ver |> previewEnsure |> resetPrev


let setup setParams =
    let p = setParams defaultPubParams
    Target "Pub" <| (fun _ ->
        let rep = p.WorkingDir
        let cmd = parseArgs p.Args

        match cmd with
        | Help ->
            log "# Pub quick tutorial"
            log "Well, if you want to make a preview just type 'pub preview patch', 'pub preview minor', 'pub preview major', it just fetches the upstream (i.e. the remote branch) for validation and changes .env."
            log "Ok, now you have preview000, it is not published, it used like a marker of a preview mode for Pub, you want to make a next preview, you can just type 'pub preview' for that, it just runs publish (without tests)."
            log "If you have a preview and you want to make a release, just type 'pub preview release', it fetches the upstream, runs tests, creates a 'Publish' commit, sets a version tag, pushes (with tags) to the upstream and runs publish."
            log "Also you can shorten 'pub preview xyz' with just 'pub xyz' (e.g. 'pub patch') if you do not need a preview of course. Also all commands are case-insensitive."
            log "You can use next shorthands: preview=pre, patch=pat, minor=min, major=maj, release=rel."
            log "If you want to make a commit before a Pub command execution, you can use -m 'commit_message', like pub pre -m 'fix smth' or pub pre rel -m 'fix smth' or pub pat -m 'fix smth'. It does not work with pub preview (patch|minor|major) of course."
        | Pub pub ->
            let msg =
                match pub with
                | Release (_, msg) | PreviewRelease msg | PreviewNext msg -> msg
                | PreviewNew _ -> None
            let isRelease =
                match pub with
                | PreviewNew _ | PreviewNext _ -> false
                | Release _ | PreviewRelease _ -> true
            let isPublish =
                match pub with
                | PreviewNew _ -> false
                | Release _ | PreviewNext _ | PreviewRelease _ -> true

            trace "Pub init"

            let oldEnv = Env.read rep
            let oldVer = oldEnv |> readVerFromEnv
            let newVer = oldVer |> makeVerInc pub
            let newEnv = newVer |> writeVerToEnv oldEnv

            let newVerVp = newVer |> verToVp false
            let newVerVsOpt = newVer |> verToVsOpt
            let bp =
                { buildParams with
                    VersionPrefix = newVerVp |> Some
                    VersionSuffix = newVerVsOpt
                }
            buildParams <- bp

            trace "Pub validation"

            match pub with
            | PreviewNext _ -> ()
            | _ ->  rep |> gitFetch
            let status = rep |> gitGetSyncStatus
            if status.Behind then failwith "there are unmerged commits on the upstream branch, merge them first"

            rep |> gitAddAll
            match msg with
            | Some _ ->
                rep |> gitEnsureStageNonempty "you can not make a commit without changes"
            | None ->
                if isRelease && status.HasUpstream && not status.Ahead then failwith "there are no commits to push, you can not make a release without them"

            if isPublish then
                trace "Pub pack"
                TargetHelper.run "Pack"
                if TargetHelper.GetErrors() |> List.isEmpty |> not then failwith "pack failed"
            if isRelease then runTests bp

            trace "Pub .env change"

            newEnv |> Env.write rep
            let stageEnv _ = Env.envFileName |> StageFile rep |> ignore

            if not isRelease then stageEnv()
            msg |> Option.iter (fun msg ->
                trace "Pub custom commit"
                msg |> gitCommit rep
            )

            if isRelease then
                trace "Pub publish commit and push"
                stageEnv()
                let verStr = newVer |> verToTagStr
                let publishMsg = sprintf "Publish %s" verStr
                publishMsg |> gitCommit rep
                verStr |> tag rep
                rep |> gitPush

            if isPublish then
                trace "Pub publish"
                publish bp

            ()
    )
