module Steinpilz.DevFlow.Fake.Lib
open FSharp.Collections.ParallelSeq
open Fake
open System

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
    SolutionFiles: FileIncludes
    UseNuGetToPack: bool
    UseDotNetCliToPack: bool
    UseDotNetCliToBuild: bool
    UseDotNetCliToRestore: bool
    UseNuGetToRestore: bool
    AssemblyInfoFiles: FileIncludes
    UseDotNetCliToTest: bool
    SetVersionForPack: bool
    DegreeOfParallelism: int
    DisableRestore: bool

    XUnitConsoleToolPath: string
    XUnitConsoleToolPathPattern: string
    XUnitTargetFrameworks: string list option;
    XUnitTimeOut: TimeSpan option
        
    AppProjects: FileIncludes
    TestProjects: FileIncludes
    PublishProjects: FileIncludes

    VersionPrefix: string
    VersionSuffix: string

    NuGetFeed: NuGetFeed
    NuGetSymbolFeed: NuGetFeed
  //  NugetTool: bool

    IncludeSymbols: bool
    EmbedSources: bool
}


let defaultBuildParams = 
    // Properties
    let artifactsDir = ".artifacts" |> FullName
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

        VersionPrefix = getBuildParamOrDefault "vp" ""
        VersionSuffix = getBuildParamOrDefault "vs" ""

        NuGetFeed = 
            {
                EndpointUrl = "https://api.nuget.org/v3/index.json"
                ApiKey = None
            }
        NuGetSymbolFeed =
            {
                EndpointUrl = "https://api.nuget.org/v3/index.json"
                ApiKey = None
            }
        //  NugetTool = false

        IncludeSymbols = true
        EmbedSources = true
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
            let testProjectName = FileHelper.filename testProjectPath
            let outputDir = param.TestDir @@ testProjectName
            FileHelper.CreateDir outputDir

            [testProjectPath]
            |> MSBuild outputDir "Build" 
                    [
                        "Configuration", "Debug"
                        "Platform", "Any CPU"
                    ]
            |> Log "AppBuild-Output: " 

        FileHelper.CreateDir param.TestOutputDir
    
        !! param.TestDlls
            |> Fake.Testing.XUnit2.xUnit2 (fun p ->  
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
        |> PSeq.iter (fun proj -> DotNetCli.Test (fun p -> { p with Project = proj }))

    let runTests() = 
        tracefn("Running tests...")
        if param.UseDotNetCliToTest then runTestsWithDotNetCli()
        else runTestsWithXUnit()
            
    let ensureSuccessExitCode code =
        if code > 0 then
            failwith (sprintf "Exit code %i doesn't indicate succeed" code)
        else
            ()

    let nugetParams = NuGetDefaults()

    let runNuGet args dir =
        ExecProcess (fun info -> 
            info.FileName <- nugetParams.ToolPath
            info.WorkingDirectory <- dir
            info.Arguments <- args) nugetParams.TimeOut
        
    let runDotNet args dir =
        ExecProcess (fun info -> 
            info.FileName <- "dotnet"
            info.WorkingDirectory <- dir
            info.Arguments <- args) nugetParams.TimeOut
        
    let packProjectsWithNuget projects (versionSuffix: Option<string>) =
        CreateDir param.PublishDir

        let mainVersion = param.VersionPrefix
        let assemblyVersion = environVarOrNone "AssemblyVersion"
        match assemblyVersion with
        | None -> ()
        | Some v -> ReplaceAssemblyInfoVersionsBulk param.AssemblyInfoFiles (fun f -> 
            { f with 
                AssemblyVersion = v
                AssemblyFileVersion = v
            }
        )

        let fullVersion = match param.VersionSuffix with 
                            | "" -> mainVersion
                            | x  -> (mainVersion + "-"+ x)

        let suffixArg = match versionSuffix with
                        | None -> ""
                        | Some x -> sprintf " -suffix %s" x

        let toolArg = (if param.IncludeSymbols then "-Symbols " else "")

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
        tracefn "Packing project %A" projects
        projects
        |> MSBuild param.PublishDir "Restore;Pack" 
                ([
                    "PackageOutputPath", param.PublishDir
                    "DebugSymbols", "false"
                    "DebugType", "Full"
                    "Configuration", "Release"
                    "Platform", "Any CPU"
                    "IncludeSymbols", param.IncludeSymbols.ToString()
                    "EmbedAllSources", param.EmbedSources.ToString()
                ] @
                if not param.SetVersionForPack then []
                else [
                    "VersionPrefix", param.VersionPrefix
                    "VersionSuffix",    match versionSuffix with
                                        | Some x -> x
                                        | None -> ""
                ])
        |> Log "AppBuild-Output: "    

    let packProjectsWithDotnetCli projects (versionSuffix: Option<string>) = 
        tracefn "Packing project %A" projects
        CreateDir param.PublishDir

        let vs =  match versionSuffix with
                    | Some x -> x
                    | None -> "" 
        
        projects
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (fun project -> 
            DotNetCli.Pack(fun p -> 
            { p with 
                Project = project
                OutputPath = param.PublishDir
                VersionSuffix = vs
                AdditionalArgs =
                    (
                    if not param.IncludeSymbols then []
                    else ["--include-symbols"]
                    @
                    ["/p:EmbedAllSources=" + param.EmbedSources.ToString()]
                    @
                    if not param.SetVersionForPack then []
                    else
                    [
                        "/p:VersionPrefix="+param.VersionPrefix
                        "/p:GlobalVersion="+param.VersionPrefix + vs
                        "/p:vs="+vs
                        "/p:vp="+param.VersionPrefix
                    ])
            }))


    let packProjects =
        if param.UseNuGetToPack 
        then packProjectsWithNuget
        else 
            if param.UseDotNetCliToPack 
            then packProjectsWithDotnetCli
            else packProjectsWithMsBuild


    let publish(nugetPackageFiles: FileIncludes, nuGetFeed: NuGetFeed) =
        nugetPackageFiles
        |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
        |> PSeq.iter (fun file -> 
            let args =
                sprintf "push %s -Source %s" 
                        file 
                        nuGetFeed.EndpointUrl

            let apiKeyArgs = 
                match nuGetFeed.ApiKey with 
                | Some apiKey -> sprintf " -ApiKey %s" apiKey
                | None -> ""

            runNuGet (args + apiKeyArgs) param.PublishDir |> ignore
        )


    let publish() =
        tracefn "publishing..."
        publish(
            !!(param.PublishDir @@ "*.nupkg")
            -- (param.PublishDir @@ "*.symbols.nupkg"),
            param.NuGetFeed)

        tracefn "publishing symbols..."
        publish(
            !!(param.PublishDir @@ "*.symbols.nupkg"),
            param.NuGetSymbolFeed)


    // Targets
    Target "Clean" (fun _ -> 
        CleanDir param.ArtifactsDir
    )

    Target "Restore" (fun _ ->
        if not param.DisableRestore then
            if param.UseDotNetCliToRestore then
                param.SolutionFiles
                    |> Seq.iter(fun project -> 
                        DotNetCli.Restore(fun rp -> 
                        { rp with 
                            Project = project
                        })
                    )
            else if param.UseNuGetToRestore then
                param.SolutionFiles
                    |> Seq.iter(fun f -> 
                        runNuGet (sprintf "restore %s" f) "" |> ensureSuccessExitCode
                    )
            else
                param.SolutionFiles
                    |> MSBuild param.BuildDir "Restore" 
                        [
                            "DebugSymbols", "false"
                            "DebugType", "Full"
                            "Configuration", "Release"
                            "Platform", "Any CPU"
                        ]
                    |> Log "AppBuild-Output: "  

    )

    Target "Build" (fun _ ->
        if param.UseDotNetCliToBuild then
            param.AppProjects
            |> PSeq.withDegreeOfParallelism param.DegreeOfParallelism
            |> PSeq.iter (fun proj -> DotNetCli.Build (fun p ->
                { p with
                    Project = proj
                    Configuration = "Release"
                    Output = param.BuildDir
                }))
        else
            param.AppProjects
                |> MSBuild param.BuildDir "Build" 
                    [
                        "DebugSymbols", "false"
                        "DebugType", "Full"
                        "Configuration", "Release"
                        "Platform", "Any CPU"
                    ]
                |> Log "AppBuild-Output: " 
    )

    Target "Test" (fun _ -> 
        runTests()
    )

    Target "Watch" (fun _ ->
        use watcher = !! ("src/**/" @@ "*.cs") |> WatchChanges (fun changes ->
            runTests()
        )
        System.Console.ReadLine() |> ignore
        watcher.Dispose()
    )

    Target "Pack" (fun _ ->
        let vs = match param.VersionSuffix with
                 | null | "" -> None
                 | s -> Some s
        packProjects param.PublishProjects vs    
    )

    //Target "Pack-Pre" (fun _ -> 
    //    packProjects param.PublishProjects (Some (match param.VersionSuffix with 
    //                                                 | "" -> "no-version"
    //                                                 | x -> x ) )
    //)

    Target "Publish" <| fun _ ->
        publish()

    //Target "Publish-Release" (fun _ ->
    //    publish()
    //)

    //Target "Publish-Pre" (fun _ ->
    //    publish()
    //)
    //
    //Target "Publish-Tags" (fun _ ->
    //    Git.Branches.
    //)

    //Pub.setup id

    Target "Default" <| DoNothing

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