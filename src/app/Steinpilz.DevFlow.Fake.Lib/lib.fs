module Steinpilz.DevFlow.Fake.Lib
open System
open FSharp.Collections.ParallelSeq
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.IO.FileSystemOperators
open Fake.DotNet.Testing

type NuGetFeed = {
    EndpointUrl: string
    ApiKey: string option 
}

type BuildParams = {
    ArtifactsDir: string
    BuildDir: string
    TestDir: string
    PublishDir: string
    TestDlls: string
    TestOutputDir: string
    SolutionFiles: IGlobbingPattern
    UseNuGetToPack: bool
    UseDotNetCliToPack: bool
    UseDotNetCliToBuild: bool
    UseDotNetCliToRestore: bool
    UseNuGetToRestore: bool
    AssemblyInfoFiles: IGlobbingPattern
    UseDotNetCliToTest: bool
    SetVersionForPack: bool
    DegreeOfParallelism: int
    DisableRestore: bool

    XUnitConsoleToolPath: string
    XUnitConsoleToolPathPattern: string
    XUnitTargetFrameworks: string list option;
    XUnitTimeOut: TimeSpan option

    AppProjects: IGlobbingPattern
    TestProjects: IGlobbingPattern
    PublishProjects: IGlobbingPattern

    VersionPrefix: string
    VersionSuffix: string

    NuGetFeed: NuGetFeed
  //  NugetTool: bool
}


let defaultBuildParams = 
    // Properties
    let artifactsDir = ".artifacts" |> Path.getFullName
    let buildDir = artifactsDir @@ "build"
    let testDir = artifactsDir @@ "test"
    let publishDir = artifactsDir @@ "publish"
    let testDlls = testDir @@ "**\\" @@ "*.Tests.dll"
    let testOutputDir = testDir @@ "output"

    let xUnitConsole = @"packages\xunit.runner.console\tools\xunit.console.exe"
    let xUnitConsolePattern = @"packages\xunit.runner.console\tools\{TargetFramework}\xunit.console.exe"
        
    {
        ArtifactsDir = artifactsDir
        BuildDir = buildDir
        TestDir = testDir
        PublishDir = publishDir
        TestDlls = testDlls
        TestOutputDir = testOutputDir
        SolutionFiles = !!"*.sln"
        XUnitConsoleToolPath = xUnitConsole
        XUnitTargetFrameworks = None
        XUnitConsoleToolPathPattern = xUnitConsolePattern
        XUnitTimeOut = None
        UseNuGetToPack = false
        UseDotNetCliToBuild = false
        UseDotNetCliToTest = false
        UseDotNetCliToPack = false
        UseDotNetCliToRestore = false
        SetVersionForPack = true
        DisableRestore = false
        UseNuGetToRestore = false
        AssemblyInfoFiles = !!"**/*AssemblyInfo.cs" ++ "**/AssemblyInfo.fs"
        DegreeOfParallelism = 1
        
        AppProjects = !!"src/app/**/*.csproj"
        TestProjects = !!"src/test/**/*Tests.csproj"
        PublishProjects = !!"not-found"

        VersionPrefix = Environment.environVarOrDefault "vp" ""
        VersionSuffix = Environment.environVarOrDefault "vs" ""

        NuGetFeed = 
            {
                EndpointUrl = "https://api.nuget.org/v3/index.json"
                ApiKey = None
            }
      //  NugetTool = false
    }

let setup setParams =
    let param = defaultBuildParams |> setParams

    let defaultTargetFrameworks = ["net462"]

    let getXUnitToolPath() =
        match System.IO.File.Exists(param.XUnitConsoleToolPath) with 
        | true -> param.XUnitConsoleToolPath
        | false -> param.XUnitTargetFrameworks 
                    |> Option.defaultValue(defaultTargetFrameworks)
                    |> Seq.map (fun (targetFramework) -> 
                            param.XUnitConsoleToolPathPattern.Replace("{TargetFramework}", targetFramework))
                    |> Seq.filter (fun x -> System.IO.File.Exists(x))
                    |> Seq.tryHead |> Option.defaultWith(fun _ -> failwith "xunit console runner not found")
                    
    let runTestsWithXUnit() =
        // we put each test project to its own folder, 
        // while they could have different dependencies (versions)
        for testProjectPath in param.TestProjects do
            let testProjectName = System.IO.Path.GetFileName testProjectPath
            let outputDir = param.TestDir @@ testProjectName
            Directory.create outputDir

            [testProjectPath]
            |> MSBuild.run id outputDir "Build" 
                    [
                        "Configuration", "Debug"
                        "Platform", "Any CPU"
                    ]
            |> Trace.logItems "AppBuild-Output: "

        Directory.create param.TestOutputDir

        !! param.TestDlls
            |> XUnit2.run (fun p ->  
                { p with
                    ToolPath = getXUnitToolPath()
                    HtmlOutputPath = Some (param.TestOutputDir @@ "test-result.html")
                    NUnitXmlOutputPath = Some (param.TestOutputDir @@ "nunit-test-result.xml")
                    Parallel = Testing.XUnit2.ParallelMode.All
                    TimeOut =  match param.XUnitTimeOut with
                                | None -> p.TimeOut
                                | Some x -> x
                } 
                )
    let runTestsWithDotNetCli() =
        param.TestProjects
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (fun proj -> DotNet.test id proj)

    let runTests() =
        Trace.tracefn "Running tests..."
        if param.UseDotNetCliToTest then runTestsWithDotNetCli()
        else runTestsWithXUnit()

    let ensureSuccessExitCode (code: ProcessResult<unit>) =
        if code.ExitCode > 0 then
            failwith (sprintf "Exit code %i doesn't indicate succeed" code.ExitCode)
        else
            ()

    let nugetParams = NuGet.NuGet.NuGetDefaults()

    let runNuGet args dir =
        CreateProcess.fromRawCommandLine nugetParams.ToolPath args
        |> CreateProcess.withWorkingDirectory dir
        |> CreateProcess.withTimeout nugetParams.TimeOut
        |> Proc.run

    let packProjectsWithNuget projects (versionSuffix: Option<string>) =
        Directory.create param.PublishDir

        let mainVersion = param.VersionPrefix
        let assemblyVersion = Environment.environVarOrNone "AssemblyVersion"
        match assemblyVersion with
        | None -> ()
        | Some v ->
            param.AssemblyInfoFiles
            |> Seq.iter (fun f -> AssemblyInfoFile.updateAttributes f [ AssemblyInfo.Version v; AssemblyInfo.FileVersion v ])

        let fullVersion = match param.VersionSuffix with 
                            | "" -> mainVersion
                            | x  -> (mainVersion + "-"+ x)

        let suffixArg = match versionSuffix with
                        | None -> ""
                        | Some x -> sprintf " -suffix %s" x

        let toolArg = if false then "-Tool " else "-IncludeReferencedProjects "

        let versionArg = if param.SetVersionForPack then sprintf "-version %s%s" mainVersion suffixArg else ""
        let globalVersionArg = if param.SetVersionForPack then sprintf "-properties globalversion=%s" fullVersion else ""
        
        projects
            |> Seq.iter (fun (projPath) ->
                let args = 
                    sprintf "pack %s -Build %s %s %s" 
                        projPath
                        versionArg 
                        toolArg
                        globalVersionArg

                runNuGet args <| param.PublishDir |> ignore
                ()
            )

    let packProjectsWithMsBuild projects (versionSuffix: Option<string>) = 
        Trace.tracefn "Packing project %A" projects
        projects
        |> MSBuild.run id param.PublishDir "Restore;Pack"
                ([
                    "PackageOutputPath", param.PublishDir
                    "DebugSymbols", "false"
                    "DebugType", "Full"
                    "Configuration", "Release"
                    "Platform", "Any CPU"
                    
                ] @
                if not param.SetVersionForPack then []
                else [
                    "VersionPrefix", param.VersionPrefix
                    "VersionSuffix",    match versionSuffix with
                                        | Some x -> x
                                        | None -> ""
                ])
        |> Trace.logItems "AppBuild-Output: "

    let packProjectsWithDotnetCli projects (versionSuffix: Option<string>) = 
        Trace.tracefn "Packing project %A" projects
        Directory.create param.PublishDir

        let vs =  match versionSuffix with
                    | Some x -> x
                    | None -> ""

        projects
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (
            DotNet.pack (fun p ->
            { p with 
                OutputPath = Some param.PublishDir
                VersionSuffix = Some vs
                MSBuildParams =
                { MSBuild.CliArguments.Create() with
                    Properties =
                        if not param.SetVersionForPack then []
                        else
                            [
                            ("VersionPrefix", param.VersionPrefix)
                            ("GlobalVersion", (param.VersionPrefix + vs))
                            ("vs", vs)
                            ("vp", param.VersionPrefix)
                            ]
                }
            })
        )

    let packProjects =
        if param.UseNuGetToPack 
        then packProjectsWithNuget
        else 
            if param.UseDotNetCliToPack 
            then packProjectsWithDotnetCli
            else packProjectsWithMsBuild


    let publish() =
        Trace.tracefn "publishing..."

        let nugetPackageFiles =
            !! (param.PublishDir @@ "*.nupkg")
            -- (param.PublishDir @@ "*.symbols.nupkg")

        nugetPackageFiles
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (fun file -> 
            let args =
                sprintf "push %s -Source %s" 
                        file 
                        param.NuGetFeed.EndpointUrl

            let apiKeyArgs = 
                match param.NuGetFeed.ApiKey with 
                | Some apiKey -> sprintf " -ApiKey %s" apiKey
                | None -> ""

            runNuGet (args + apiKeyArgs) param.PublishDir |> ignore
        )

    // Targets
    Target.create "Clean" (fun _ -> 
        Shell.cleanDir param.ArtifactsDir
    )

    Target.create "Restore" (fun _ ->
        if not param.DisableRestore then
            if param.UseDotNetCliToRestore then
                param.SolutionFiles
                    |> Seq.iter (DotNet.restore id)
            else if param.UseNuGetToRestore then
                param.SolutionFiles
                    |> Seq.iter(fun f -> 
                        runNuGet (sprintf "restore %s" f) "" |> ensureSuccessExitCode
                    )
            else
                param.SolutionFiles
                    |> MSBuild.run id param.BuildDir "Restore"
                        [
                            "DebugSymbols", "false"
                            "DebugType", "Full"
                            "Configuration", "Release"
                            "Platform", "Any CPU"
                        ]
                    |> Trace.logItems "AppBuild-Output: "

    )

    Target.create "Build" (fun _ ->
        if param.UseDotNetCliToBuild then
            param.AppProjects
            |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
            |> PSeq.iter (fun proj ->
                DotNet.build (fun p ->
                    { p with
                        Configuration = DotNet.BuildConfiguration.Release
                        OutputPath= Some param.BuildDir
                    })
                    proj)
        else
            param.AppProjects
                |> MSBuild.run id param.BuildDir "Build" 
                    [
                        "DebugSymbols", "false"
                        "DebugType", "Full"
                        "Configuration", "Release"
                        "Platform", "Any CPU"
                    ]
                |> Trace.logItems "AppBuild-Output: " 
    )

    Target.create "Test" (fun _ -> 
        runTests()
    )

    Target.create "Watch" (fun _ ->
        use watcher = !! ("src/**/" @@ "*.cs") |> ChangeWatcher.run (fun _ -> runTests())
        System.Console.ReadLine() |> ignore
        watcher.Dispose()
    )

    Target.create "Pack" (fun _ ->
        let vs = match param.VersionSuffix with
                 | null | "" -> None
                 | s -> Some s
        packProjects param.PublishProjects vs    
    )

    //Target.create "Pack-Pre" (fun _ -> 
    //    packProjects param.PublishProjects (Some (match param.VersionSuffix with 
    //                                                 | "" -> "no-version"
    //                                                 | x -> x ) )
    //)

    Target.create "Publish" <| fun _ ->
        publish()

    //Target.create "Publish-Release" (fun _ ->
    //    publish()
    //)

    //Target.create "Publish-Pre" (fun _ ->
    //    publish()
    //)
    //
    //Target.create "Publish-Tags" (fun _ ->
    //    Git.Branches.
    //)

    //Pub.setup id

    Target.create "Default" |> ignore

    // Dependencies
    "Clean"
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
