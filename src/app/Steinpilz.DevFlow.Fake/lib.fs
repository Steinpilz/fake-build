module Steinpilz.DevFlow.Fake.Lib
open Fake

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
    UseNuGetToRestore: bool
    AssemblyInfoFiles: FileIncludes

    XUnitConsoleToolPath: string
        
    AppProjects: FileIncludes
    TestProjects: FileIncludes
    PublishProjects: FileIncludes

    VersionPrefix: string
    VersionSuffix: string

    NuGetFeed: NuGetFeed
}


let defaultBuildParams = 
    // Properties
    let artifactsDir = ".artifacts" |> FullName
    let buildDir = artifactsDir @@ "build"
    let testDir = artifactsDir @@ "test"
    let publishDir = artifactsDir @@ "publish"
    let testDlls = testDir @@ "*.Tests.dll"
    let testOutputDir = testDir @@ "output"

    let xUnitConsole = @"packages\xunit.runner.console\tools\xunit.console.exe"
        
    {
        ArtifactsDir = artifactsDir
        BuildDir = buildDir
        TestDir = testDir
        PublishDir = publishDir
        TestDlls = testDlls
        TestOutputDir = testOutputDir
        SolutionFiles = !!"*.sln"
        XUnitConsoleToolPath = xUnitConsole
        UseNuGetToPack = false
        UseDotNetCliToPack = false
        UseNuGetToRestore = false
        AssemblyInfoFiles = !!"**/*AssemblyInfo.cs" ++ "**/AssemblyInfo.fs"
        
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
    }

let setup setParams =
    let param = defaultBuildParams |> setParams
        

    let runTests() = 
        tracefn("Running tests...")
        param.TestProjects
        |> MSBuild param.TestDir "Build" 
                [
                    "Configuration", "Debug"
                    "Platform", "Any CPU"
                ]
        |> Log "AppBuild-Output: " 

        FileHelper.CreateDir param.TestOutputDir
    
        !! param.TestDlls
            |> Fake.Testing.XUnit2.xUnit2 (fun p ->  
                { p with
                    ToolPath = param.XUnitConsoleToolPath
                    HtmlOutputPath = Some (param.TestOutputDir + "test-result.html")
                    NUnitXmlOutputPath = Some (param.TestOutputDir + "nunit-test-result.xml")
                    Parallel = Testing.XUnit2.ParallelMode.All
                } 
                )

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
                        | Some x -> sprintf "-suffix %s" x

        

        projects
            |> Seq.iter (fun (projPath) ->
                let args = 
                    sprintf "pack %s -Build -includereferencedprojects -version %s %s -properties %s" 
                        projPath
                        mainVersion 
                        suffixArg
                        "globalversion=" + fullVersion
                
                runNuGet args <| param.PublishDir |> ignore
                ()
            )
    let packProjectsWithMsBuild projects (versionSuffix: Option<string>) = 
        tracefn "Packing project %A" projects
        projects
        |> MSBuild param.PublishDir "Restore;Pack" 
                [
                    "DebugSymbols", "false"
                    "DebugType", "Full"
                    "Configuration", "Release"
                    "Platform", "Any CPU"
                    "VersionPrefix", param.VersionPrefix
                    "VersionSuffix",    match versionSuffix with
                                        | Some x -> x
                                        | None -> ""
                ]
        |> Log "AppBuild-Output: "    

    let packProjectsWithDotnetCli projects (versionSuffix: Option<string>) = 
        tracefn "Packing project %A" projects
        CreateDir param.PublishDir

        projects
        |> Seq.iter (fun project -> 
            DotNetCli.Pack(fun p -> 
            { p with 
                Project = project
                OutputPath = param.PublishDir
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
        tracefn "publishing..."

        let parameters = NuGetDefaults()
        let nugetPackageFiles = 
            !!(param.PublishDir @@ "*.nupkg")
            -- (param.PublishDir @@ "*.symbols.nupkg")

        nugetPackageFiles
        |> Seq.iter (fun file -> 
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
    Target "Clean" (fun _ -> 
        CleanDir param.ArtifactsDir
    )

    Target "Restore" (fun _ -> 
        if param.UseNuGetToRestore then
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
        packProjects param.PublishProjects None
        
    )

    Target "Pack-Pre" (fun _ -> 
        packProjects param.PublishProjects (Some (match param.VersionSuffix with 
                                                     | "" -> "no-version"
                                                     | x -> x ) )
    )

    Target "Publish-Release" (fun _ ->
        publish()
    )

    Target "Publish-Pre" (fun _ ->
        publish()
    )
    //
    //Target "Publish-Tags" (fun _ ->
    //    Git.Branches.
    //)

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
        ==> "Pack-Pre"
        |> ignore

    "Clean"
        ==> "Pack"
        |> ignore

    "Pack"
        ==> "Publish-Release"
        |> ignore

    "Pack-Pre"
        ==> "Publish-Pre"
        |> ignore

    ()

    RunTargetOrDefault "Watch"