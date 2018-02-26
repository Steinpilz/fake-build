[<AutoOpen>]
module BuildUtils
open BuildParams
open Fake

let clean param =
    CleanDir param.ArtifactsDir

let runTests param =
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

let watch param =
    use _watcher = !! ("src/**/" @@ "*.cs") |> WatchChanges (fun _changes -> runTests param)
    System.Console.ReadLine() |> ignore

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

let fullVersion vp vs =
    (vp |> Option.defaultValue "") + (vs |> Option.map ((+) "-") |> Option.defaultValue "")

let packProjectsWithNuget param projects versionPrefix versionSuffix =
    CreateDir param.PublishDir
    let mainVersion = versionPrefix |> Option.defaultValue ""

    param.AssemblyVersion |> Option.iter (fun v ->
        ReplaceAssemblyInfoVersionsBulk param.AssemblyInfoFiles (fun f ->
        { f with
            AssemblyVersion = v
            AssemblyFileVersion = v
        })
    )

    let fv = (versionPrefix, param.VersionSuffix) ||> fullVersion

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
                    "globalversion=" + fv

            param.PublishDir |> runNuGet args |> ignore
        )

let packProjectsWithMsBuild param projects versionPrefix versionSuffix =
    tracefn "Packing project %A" projects
    let vp = versionPrefix |> Option.defaultValue ""
    let vs = versionSuffix |> Option.defaultValue ""
    projects
    |> MSBuild param.PublishDir "Restore;Pack"
            [
                "PackageOutputPath", param.PublishDir
                "DebugSymbols", "false"
                "DebugType", "Full"
                "Configuration", "Release"
                "Platform", "Any CPU"
                "VersionPrefix", vp
                "VersionSuffix", vs
            ]
    |> Log "AppBuild-Output: "

let packProjectsWithDotnetCli param projects versionPrefix versionSuffix=
    tracefn "Packing project %A" projects
    CreateDir param.PublishDir

    let vp = versionPrefix |> Option.defaultValue ""
    let vs = versionSuffix |> Option.defaultValue ""
    let fv = (versionPrefix, versionSuffix) ||> fullVersion

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
                "/p:GlobalVersion=" + fv
                "/p:vs=" + vs
                "/p:vp=" + vp
            ]
        })
    )

let packProjects param =
    if param.UseNuGetToPack
    then packProjectsWithNuget param
    else
        if param.UseDotNetCliToPack
        then packProjectsWithDotnetCli param
        else packProjectsWithMsBuild param


let publish param =
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

let build param =
    param.AppProjects |> MSBuild param.BuildDir "Build"
        [
            "DebugSymbols", "false"
            "DebugType", "Full"
            "Configuration", "Release"
            "Platform", "Any CPU"
        ]
        |> Log "AppBuild-Output: "

let restore param =
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
