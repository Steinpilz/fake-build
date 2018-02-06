module Steinpilz.DevFlow.Fake.Lib
open Fake
open System
open Env
open Pub


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
    XUnitTimeOut: TimeSpan option

    AppProjects: FileIncludes
    TestProjects: FileIncludes
    PublishProjects: FileIncludes

    VersionPrefix: string option
    VersionSuffix: string option
    AssemblyVersion: string option

    NuGetFeed: NuGetFeed
    //  NugetTool: bool
}


let defaultBuildParams =
    "." |> FullName |> Env.read |> setEnvVars

    // Properties
    let artifactsDir = ".artifacts" |> FullName
    let buildDir = artifactsDir @@ "build"
    let testDir = artifactsDir @@ "test"
    let publishDir = artifactsDir @@ "publish"
    let testDlls = testDir @@ "**/" @@ "*.Tests.dll"
    let testOutputDir = testDir @@ "output"

    let xUnitConsole = @"packages/xunit.runner.console/tools/net452/xunit.console.exe"

    let assemblyVersion = environVarOrNone "AssemblyVersion"
    let noneIfEmpty = Option.filter (String.IsNullOrEmpty >> not)
    let vp = environVarOrNone "vp" |> noneIfEmpty
    let vs = environVarOrNone "vs" |> noneIfEmpty

    {
        ArtifactsDir = artifactsDir
        BuildDir = buildDir
        TestDir = testDir
        PublishDir = publishDir
        TestDlls = testDlls
        TestOutputDir = testOutputDir
        SolutionFiles = !!"*.sln"
        XUnitConsoleToolPath = xUnitConsole
        XUnitTimeOut = None
        UseNuGetToPack = false
        UseDotNetCliToPack = true
        UseNuGetToRestore = false
        AssemblyInfoFiles = !!"**/*AssemblyInfo.cs" ++ "**/AssemblyInfo.fs"

        AppProjects = !!"src/app/**/*.(csproj|fsproj)"
        TestProjects = !!"src/test/**/*Tests.(csproj|fsproj)"
        PublishProjects = !!"not-found"

        VersionPrefix = vp
        VersionSuffix = vs
        AssemblyVersion = assemblyVersion

        NuGetFeed =
            {
                EndpointUrl = "https://api.nuget.org/v3/index.json"
                ApiKey = None
            }
        //  NugetTool = false
    }

let setup setParams =
    let param = defaultBuildParams |> setParams


    let runTests() =
        tracefn("Running tests...")

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

        let testDlls = !! param.TestDlls |> List.ofSeq
        if not (List.isEmpty testDlls) then
            testDlls
                |> Fake.Testing.XUnit2.xUnit2 (fun p ->
                    { p with
                        ToolPath = param.XUnitConsoleToolPath
                        HtmlOutputPath = Some (param.TestOutputDir @@ "test-result.html")
                        NUnitXmlOutputPath = Some (param.TestOutputDir @@ "nunit-test-result.xml")
                        Parallel = Testing.XUnit2.ParallelMode.All
                        TimeOut = param.XUnitTimeOut |> Option.defaultValue p.TimeOut
                    }
                    )
        else tracefn "There are no test dlls"

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
            info.Arguments <- args
        ) nugetParams.TimeOut

    let runDotNet args dir =
        ExecProcess (fun info ->
            info.FileName <- "dotnet"
            info.WorkingDirectory <- dir
            info.Arguments <- args
        ) nugetParams.TimeOut

    let packProjectsWithNuget projects versionSuffix =
        CreateDir param.PublishDir
        let mainVersion = param.VersionPrefix |> Option.defaultValue ""

        param.AssemblyVersion |> Option.iter (fun v ->
            ReplaceAssemblyInfoVersionsBulk param.AssemblyInfoFiles (fun f ->
            { f with
                AssemblyVersion = v
                AssemblyFileVersion = v
            })
        )

        let fullVersion = mainVersion + (param.VersionSuffix |> Option.map ((+) "-") |> Option.defaultValue "")

        let suffixArg = versionSuffix |> Option.map (sprintf " -suffix %s") |> Option.defaultValue ""

        let toolArg = if false then "-Tool " else "-IncludeReferencedProjects "

        projects
            |> Seq.iter (fun (projPath) ->
                let args =
                    sprintf "pack %s -Build -version %s%s %s -properties %s"
                        projPath
                        mainVersion
                        suffixArg
                        toolArg
                        "globalversion=" + fullVersion

                param.PublishDir |> runNuGet args |> ignore
            )

    let packProjectsWithMsBuild projects versionSuffix =
        tracefn "Packing project %A" projects
        projects
        |> MSBuild param.PublishDir "Restore;Pack"
                [
                    "PackageOutputPath", param.PublishDir
                    "DebugSymbols", "false"
                    "DebugType", "Full"
                    "Configuration", "Release"
                    "Platform", "Any CPU"
                    "VersionPrefix", param.VersionPrefix |> Option.defaultValue ""
                    "VersionSuffix", versionSuffix |> Option.defaultValue ""
                ]
        |> Log "AppBuild-Output: "

    let packProjectsWithDotnetCli projects versionSuffix=
        tracefn "Packing project %A" projects
        CreateDir param.PublishDir

        let vp = param.VersionPrefix |> Option.defaultValue ""
        let vs = versionSuffix |> Option.defaultValue ""

        projects
        |> Seq.iter (fun project ->
            DotNetCli.Pack(fun p ->
            { p with
                Project = project
                OutputPath = param.PublishDir
                VersionSuffix = vs
                AdditionalArgs =
                [
                    "/p:VersionPrefix=" + vp
                    "/p:GlobalVersion=" + vp + vs
                    "/p:vs=" + vs
                    "/p:vp=" + vp
                ]
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

            let apiKeyArgs = param.NuGetFeed.ApiKey |> Option.map (sprintf " -ApiKey %s") |> Option.defaultValue ""

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
        param.AppProjects |> MSBuild param.BuildDir "Build"
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
        packProjects param.PublishProjects param.VersionSuffix
    )

    Target "Pack-Pre" (fun _ ->
        packProjects param.PublishProjects (param.VersionSuffix |> Option.defaultValue "no-version" |> Some)
    )

    Target "Publish" <| fun _ ->
        publish()

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

    Pub.setup id

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
        ==> "Pack-Pre"
        |> ignore

    "Clean"
        ==> "Restore"
        ==> "Pack"
        |> ignore

    "Pack"
        ==> "Publish-Release"
        |> ignore

    "Pack"
        ==> "Publish"
        |> ignore

    "Pack-Pre"
        ==> "Publish-Pre"
        |> ignore

    ()

    param
