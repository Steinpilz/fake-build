#r "paket:
framework: netstandard20
nuget Fake.DotNet.Cli
nuget Fake.IO.FileSystem
nuget Fake.Core.Target //"

#r @"../src/app/Steinpilz.DevFlow.Fake.Lib/bin/Debug/netstandard2.0/Steinpilz.DevFlow.Fake.Lib.dll"

#load ".fake/build.fsx/intellisense.fsx"

open Fake.DotNet
open Fake.Core
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators
open Steinpilz.DevFlow.Fake

let param = Lib.setup <| fun p -> 
    { p with 
        AppProjects = !!"src/**/*.fsproj"
        TestProjects = !!"test/**/*.fsproj"
        PublishProjects = !! "src/**/*.fsproj"
        UseDotNetCliToBuild = true
        UseDotNetCliToPack = true
        UseDotNetCliToRestore = true
        NuGetFeed = 
            { p.NuGetFeed with 
                ApiKey = Environment.environVarOrFail <| "NUGET_API_KEY" |> Some
            }
    }

let packTool version =
    Directory.create param.PublishDir
    NuGet.NuGet.NuGetPack(fun p ->
    {p with
        Version = version
        WorkingDir = param.ArtifactsDir @@ "build"
        OutputPath = param.PublishDir
        Files =
            [
                (@"**/*", Some "tools", None)
            ]
    }) ("src/app/Steinpilz.DevFlow.Fake/Steinpilz.DevFlow.Fake.nuspec" |> Path.getFullName)

Target.create "Pack-Tool" (fun _ -> 
    let vs = match param.VersionSuffix with
                 | null | "" -> ""
                 | s -> "-" + s
    packTool <| param.VersionPrefix + vs
)

"Build"
    ==> "Pack-Tool"
    ==> "Pack"

Target.runOrDefault "Watch"
