module Steinpilz.DevFlow.Fake.Lib
open System
open FSharp.Collections.ParallelSeq
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.IO.FileSystemOperators

type NuGetFeed = {
    EndpointUrl: string
    ApiKey: string option 
}

type BuildParams = {
    ArtifactsDir: string
    BuildDir: string
    TestDir: string
    PublishDir: string
    TestOutputDir: string
    SolutionFiles: IGlobbingPattern
    AssemblyInfoFiles: IGlobbingPattern
    SetVersionForPack: bool
    DegreeOfParallelism: int
    DisableRestore: bool

    AppProjects: IGlobbingPattern
    TestProjects: IGlobbingPattern
    PublishProjects: IGlobbingPattern

    VersionPrefix: string
    VersionSuffix: string

    NuGetFeed: NuGetFeed

    PackOptions: DotNet.PackOptions -> DotNet.PackOptions
    BuildOptions: DotNet.BuildOptions -> DotNet.BuildOptions
    NuGetPushOptions: DotNet.NuGetPushOptions -> DotNet.NuGetPushOptions
    TestOptions: DotNet.TestOptions -> DotNet.TestOptions
}

let defaultBuildParams = 
    // Properties
    let artifactsDir = ".artifacts" |> Path.getFullName
    let buildDir = artifactsDir @@ "build"
    let testDir = artifactsDir @@ "test"
    let publishDir = artifactsDir @@ "publish"
    let testOutputDir = testDir @@ "output"
        
    {
        ArtifactsDir = artifactsDir
        BuildDir = buildDir
        TestDir = testDir
        PublishDir = publishDir
        TestOutputDir = testOutputDir
        SolutionFiles = !!"*.sln"
        SetVersionForPack = true
        DisableRestore = false
        AssemblyInfoFiles = !!"**/*AssemblyInfo.cs" ++ "**/AssemblyInfo.fs"
        DegreeOfParallelism = 1
        
        AppProjects = !!"src/app/**/*.*sproj"
        TestProjects = !!"src/test/**/*Tests.*sproj"
        PublishProjects = !!"not-found"

        VersionPrefix = Environment.environVarOrDefault "vp" ""
        VersionSuffix = Environment.environVarOrDefault "vs" ""

        NuGetFeed = 
            {
                EndpointUrl = "https://api.nuget.org/v3/index.json"
                ApiKey = None
            }
        PackOptions = id
        BuildOptions = id
        NuGetPushOptions = id
        TestOptions = id
    }

let setup setParams =
    let param = defaultBuildParams |> setParams

    let info() =
        Trace.logfn ""
        Trace.logfn "******** PARAMETERS **********"
        Trace.logfn "BuildDir:\t %s" param.BuildDir
        Trace.logfn "TestDir:\t %s" param.TestDir
        Trace.logfn "ArtifactsDir:\t %s" param.ArtifactsDir
        Trace.logfn "PublishDir:\t %s" param.PublishDir
        Trace.logfn "TestOutputDir:\t %s" param.TestOutputDir
        Trace.logfn "SolutionFiles:\t %s" (param.SolutionFiles.ToString())
        Trace.logfn "AssemblyInfoFiles:\t %s" (param.AssemblyInfoFiles.ToString())
        Trace.logfn "SetVersionForPack:\t %b" param.SetVersionForPack
        Trace.logfn "DegreeOfParallelism:\t %d" param.DegreeOfParallelism
        Trace.logfn "DisableRestore:\t %b" param.DisableRestore
        Trace.logfn "AppProjects:\t %s" (param.AppProjects.ToString())
        Trace.logfn "TestProjects:\t %s" (param.AppProjects.ToString())
        Trace.logfn "PublishProjects:\t %s" (param.AppProjects.ToString())
        Trace.logfn "VersionPrefix:\t %s" param.VersionPrefix
        Trace.logfn "VersionSuffix:\t %s" param.VersionSuffix
        Trace.logfn "NuGetFeed.Endpoint:\t %s" param.NuGetFeed.EndpointUrl
        Trace.logfn "NuGetFeed.ApiKey.Length:\t %d" (param.NuGetFeed.ApiKey |> Option.map(fun s -> s.Length) |> Option.defaultValue 0)
        Trace.logfn ""
        Trace.logfn "********* SYSTEM INFO **********"
        
        DotNet.getVersion id |> ignore

    let runTestsWithDotNetCli() =
        param.TestProjects
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (
            DotNet.test(fun opt -> 
                { opt with
                    Configuration = DotNet.Debug
                    Output = Some param.TestOutputDir
                } |> param.TestOptions
            )
        )

    let runTests() =
        Trace.tracefn "Running tests..."
        runTestsWithDotNetCli()

    let ensureSuccessExitCode (code: ProcessResult<unit>) =
        if code.ExitCode > 0 then
            failwith (sprintf "Exit code %i doesn't indicate succeed" code.ExitCode)
        else
            ()

    let packProjectsWithDotnetCli projects (versionSuffix: Option<string>) = 
        Trace.tracefn "Packing project %A" projects
        Directory.create param.PublishDir

        let mainVersion = param.VersionPrefix
        let fullVersion = match param.VersionSuffix with 
                            | "" -> mainVersion
                            | x  -> (mainVersion + "-"+ x)
        projects
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (
            DotNet.pack (fun p ->
            { p with 
                OutputPath = Some param.PublishDir
                VersionSuffix = versionSuffix
                Configuration = DotNet.Release
                MSBuildParams =
                { p.MSBuildParams with
                    DisableInternalBinLog = true
                    Properties =
                        if not param.SetVersionForPack then []
                        else
                            [
                                ("DebugType", "Full")
                                ("VersionPrefix", param.VersionPrefix)
                                ("FullVersion", fullVersion)
                            ]
                }
            } |> param.PackOptions)
        )

    let packProjects = packProjectsWithDotnetCli

    let publish() =
        Trace.tracefn "publishing..."

        let nugetPackageFiles =
            !! (param.PublishDir @@ "*.nupkg")
            -- (param.PublishDir @@ "*.symbols.nupkg")

        nugetPackageFiles
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (fun file -> 
            DotNet.nugetPush (fun opt -> 
            { opt with 
                PushParams = 
                { opt.PushParams with 
                    ApiKey = param.NuGetFeed.ApiKey
                    Source = Some param.NuGetFeed.EndpointUrl
                }
            } |> param.NuGetPushOptions) file
        )

    // Targets
    Target.create "Info" (fun _ -> info())
    Target.create "Clean" (fun _ -> 
        Shell.cleanDir param.ArtifactsDir
    )

    Target.create "Restore" (fun _ ->
        match param.DisableRestore with
        | false -> param.SolutionFiles |> Seq.iter (DotNet.restore id)
        | _ -> ()
    )

    Target.create "Build" (fun _ ->
        param.AppProjects
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (fun proj ->
            DotNet.build (fun p ->
                { p with
                    Configuration = DotNet.BuildConfiguration.Release
                    OutputPath= Some param.BuildDir
                } |> param.BuildOptions)
                proj)
    )

    Target.create "Test" (fun _ -> 
        runTests()
    )

    Target.create "Watch" (fun _ ->
        use watcher = !! ("src/**/" @@ "*.cs") |> ChangeWatcher.run (fun _ -> runTests())
        Console.ReadLine() |> ignore
        watcher.Dispose()
    )

    Target.create "Pack" (fun _ ->
        let vs = match param.VersionSuffix with
                 | null | "" -> None
                 | s -> Some s
        packProjects param.PublishProjects vs    
    )

    Target.create "Publish" <| fun _ ->
        publish()

    Target.create "Default" ignore

    // Dependencies
    "Info"
        ==> "Clean"
        ==> "Restore"
        ==> "Build"
        ==> "Default"
        |> ignore

    "Restore"
        ==> "Test"
        |> ignore

    "Clean"
        ==> "Restore"
        |> ignore

    "Clean"
        ==> "Restore"
        ==> "Pack"
        |> ignore

    "Pack"
        ==> "Publish"
        |> ignore
    ()

    param
