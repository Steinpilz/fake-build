[<AutoOpen>]
module BuildParams
open System
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

let defaultBuildParams _ =
    // Properties
    let artifactsDir = ".artifacts" |> FullName
    let buildDir = artifactsDir @@ "build"
    let testDir = artifactsDir @@ "test"
    let publishDir = artifactsDir @@ "publish"
    let testDlls = testDir @@ "**/" @@ "*.Tests.dll"
    let testOutputDir = testDir @@ "output"

    let xUnitConsole = @"packages/xunit.runner.console/tools/net452/xunit.console.exe"

    let assemblyVersion = environVarOrNone "AssemblyVersion"
    let noneIfEmpty = Option.filter (not << String.IsNullOrWhiteSpace)
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

"." |> FullName |> Env.read |> setEnvVars
let mutable buildParams = defaultBuildParams()
